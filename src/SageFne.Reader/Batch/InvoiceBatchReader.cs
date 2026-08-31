using Microsoft.Extensions.Options;
using SageFne.Reader.Configuration;
using SageFne.Reader.Certification;
using SageFne.Reader.Data;
using SageFne.Reader.Mapping;
using SageFne.Reader.Models.Sage;
using SageFne.Reader.Validation;

namespace SageFne.Reader.Batch;

/// <summary>
/// Lit un lot de factures et les traduit, chacune indépendamment des autres.
/// </summary>
/// <remarks>
/// Trois lectures pour tout le lot — les entêtes, les lignes, les clients —
/// et non trois par facture. Sur un mois de facturation, c'est la différence
/// entre une seconde et une minute, et cela évite de tenir la base occupée
/// pendant que le lot défile.
///
/// Une pièce en défaut n'interrompt pas le lot : elle ressort marquée, les
/// autres continuent. Un comptable veut voir tout ce qui cloche en une fois,
/// pas le découvrir une erreur après l'autre.
/// </remarks>
public sealed class InvoiceBatchReader(
    ISageInvoiceRepository repository,
    IFneInvoiceMapper mapper,
    ICertificationLedger ledger,
    IOptions<FneOptions> options)
{
    private readonly FneOptions _options = options.Value;

    public async Task<InvoiceBatch> ReadAsync(InvoiceQuery query, CancellationToken cancellation = default)
    {
        var constats = new CheckReport();

        var entetes = await repository.GetInvoicesAsync(query, cancellation);
        if (entetes.Count == 0)
        {
            constats.Avertir("LOT_VIDE", $"Aucune facture pour {query.Describe()}.");
            return new InvoiceBatch { Conversions = [], Constats = constats.Constats };
        }

        if (entetes.Count >= query.Limite)
        {
            constats.Avertir(
                "LIMITE_ATTEINTE",
                $"Le lot atteint la limite de {query.Limite} pièces : il y en a peut-être davantage. " +
                "Resserrez la période ou augmentez la limite.");
        }

        // Les lignes du lot en une lecture, puis regroupées par pièce.
        var lignes = await repository.GetLinesAsync(query with { Pieces = entetes.Select(e => e.Piece).ToList() }, cancellation);
        var parPiece = lignes
            .GroupBy(ligne => ligne.Piece)
            .ToDictionary(groupe => groupe.Key, groupe => groupe.OrderBy(ligne => ligne.Ligne).ToList());

        // Les clients de même : un seul aller-retour, sans doublon.
        var comptes = entetes.Select(entete => entete.Tiers).Distinct().ToList();
        var clients = (await repository.GetCustomersAsync(comptes, cancellation))
            .ToDictionary(client => client.CtNum, StringComparer.OrdinalIgnoreCase);

        // Le registre des certifications en une lecture lui aussi.
        // Le registre est interrogé sur l'identité stable, pas sur le numéro :
        // une facture certifiée en type 6 doit rester reconnue une fois passée
        // en 7, et un bon de livraison de même numéro ne doit pas la masquer.
        var deja = await ledger.LookupAsync(entetes.Select(entete => entete.Identite).ToList(), cancellation);

        // Le même document ne doit apparaître qu'une fois. S'il ressort deux
        // fois, c'est que la comptabilisation a laissé deux lignes plutôt que
        // d'en modifier une : sans ce contrôle, il partirait deux fois.
        foreach (var double_ in entetes.GroupBy(entete => entete.Identite).Where(groupe => groupe.Count() > 1))
        {
            constats.Erreur(
                "PIECE_EN_DOUBLE",
                $"La pièce {double_.First().Piece} ressort {double_.Count()} fois " +
                $"(DO_Type {string.Join(" et ", double_.Select(entete => entete.Type).Distinct())}). " +
                "Une facture et sa version comptabilisée coexistent : le lot ne peut pas " +
                "trancher laquelle envoyer, et rien ne part tant que ce n'est pas éclairci.");
        }

        var conversions = new List<InvoiceConversion>(entetes.Count);
        foreach (var entete in entetes)
        {
            conversions.Add(Convertir(entete, parPiece, clients, deja));
        }

        return new InvoiceBatch { Conversions = conversions, Constats = constats.Constats };
    }

    private InvoiceConversion Convertir(
        SageDocumentHeader entete,
        IReadOnlyDictionary<string, List<SageDocumentLine>> parPiece,
        IReadOnlyDictionary<string, SageCustomer> clients,
        IReadOnlyDictionary<string, CertifiedInvoice> deja)
    {
        var rapport = new CheckReport();
        var lignes = parPiece.TryGetValue(entete.Piece, out var trouvees) ? trouvees : [];
        clients.TryGetValue(entete.Tiers, out var client);

        InvoiceValidator.Validate(entete, client, lignes, _options.Template, rapport);
        FinancialChecks.CompareHeader(entete, lignes, rapport);
        FinancialChecks.Run(lignes, rapport);

        // La facture n'est construite que si elle a de quoi l'être ; les
        // contrôles restent produits dans tous les cas.
        var facture = client is not null && lignes.Count > 0
            ? mapper.Map(entete, lignes, client, rapport)
            : null;

        var empreinte = facture is null ? "" : InvoiceFingerprint.Compute(facture);
        deja.TryGetValue(entete.Identite, out var certification);
        var etat = Etat(entete, facture, rapport, empreinte, certification);

        return new InvoiceConversion
        {
            Header = entete,
            Customer = client,
            Lines = lignes,
            Invoice = facture,
            Report = rapport,
            Empreinte = empreinte,
            Certification = certification,
            Etat = etat,
        };
    }

    /// <summary>
    /// Une pièce déjà certifiée ne se renvoie pas ; une pièce certifiée puis
    /// modifiée ne se tait pas non plus. C'est l'empreinte du corps envoyé qui
    /// les sépare : ce que la DGI a certifié contre ce que Sage contient
    /// aujourd'hui.
    /// </summary>
    private static EtatPiece Etat(
        SageDocumentHeader entete,
        Models.Fne.FneInvoice? facture,
        CheckReport rapport,
        string empreinte,
        CertifiedInvoice? certification)
    {
        if (certification is null)
        {
            return facture is not null && !rapport.ContientDesErreurs ? EtatPiece.ACertifier : EtatPiece.Bloquee;
        }

        var certifieeLe = certification.CertifieeLe.ToLocalTime().ToString("dd/MM/yyyy à HH:mm");

        if (certification.Empreinte == empreinte && empreinte != "")
        {
            rapport.Avertir(
                "DEJA_CERTIFIEE",
                $"Pièce {entete.Piece} certifiée le {certifieeLe}" +
                $"{(certification.ReferenceFne == "" ? "" : $" sous {certification.ReferenceFne}")} : " +
                "elle n'a pas changé depuis et ne doit pas être renvoyée.");
            return EtatPiece.DejaCertifiee;
        }

        // Certifiée, mais le corps a changé : la facture remise au client ne
        // correspond plus au document. Ce n'est pas à l'outil de trancher.
        rapport.Erreur(
            "MODIFIEE_APRES_CERTIFICATION",
            $"Pièce {entete.Piece} certifiée le {certifieeLe}, puis modifiée dans Sage. " +
            "La facture certifiée ne correspond plus au document : un avoir puis une nouvelle " +
            "facture sont sans doute nécessaires. Rien n'est renvoyé.");
        return EtatPiece.ModifieeDepuis;
    }
}
