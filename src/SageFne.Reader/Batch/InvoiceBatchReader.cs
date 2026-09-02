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

        // Le paramétrage des taxes du dossier : trois lignes, mais c'est lui
        // qui dit qu'AIRSI n'est pas une TVA malgré son TA_EdiCode « VAT ».
        var catalogue = new TaxCatalogue(
            await repository.GetTaxesAsync(cancellation),
            _options.CustomTaxes);

        // La famille d'un article ne se lit que dans F_ARTICLE, et seulement si
        // une ligne est à 0 % : sans exonération, elle ne servirait à rien.
        var familles = lignes.Any(ligne => TaxMapping.TauxTva(ligne) == 0m)
            ? await repository.GetArticleFamiliesAsync(
                lignes.Select(ligne => ligne.ArticleReference).Distinct().ToList(),
                cancellation)
            : [];

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
            conversions.Add(Convertir(entete, parPiece, clients, deja, familles, catalogue));
        }

        return new InvoiceBatch { Conversions = conversions, Constats = constats.Constats };
    }

    private InvoiceConversion Convertir(
        SageDocumentHeader entete,
        IReadOnlyDictionary<string, List<SageDocumentLine>> parPiece,
        IReadOnlyDictionary<string, SageCustomer> clients,
        IReadOnlyDictionary<string, CertifiedInvoice> deja,
        IReadOnlyDictionary<string, string> familles,
        TaxCatalogue catalogue)
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
            ? mapper.Map(entete, lignes, client, rapport, familles, catalogue)
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

        // Un envoi dont l'issue est inconnue interdit tout renvoi automatique :
        // la DGI l'a peut-être enregistré, et le doublon ne se rattrape pas.
        if (certification.Etat == Fne.EtatFne.Sending)
        {
            rapport.Erreur(
                "ENVOI_EN_SUSPENS",
                $"Pièce {entete.Piece} : un envoi est parti le {certifieeLe} et son issue est " +
                "inconnue. Vérifiez sur le portail DGI si elle a été certifiée. Si elle ne l'est " +
                "pas, débloquez-la explicitement ; si elle l'est, inscrivez sa référence. " +
                "Rien ne repart tant que ce n'est pas tranché.");
            return EtatPiece.EnSuspens;
        }

        // Déposée au portail, en attente du clic. Ce test doit rester AVANT
        // celui qui suit : « tout ce qui n'est pas Certified peut repartir »
        // rendrait renvoyable une facture déjà présente chez la DGI, et le
        // doublon serait fabriqué par la règle même qui existe pour l'empêcher.
        if (certification.Etat == Fne.EtatFne.Transmise)
        {
            rapport.Erreur(
                "TRANSMISE_ATTENTE_CLIC",
                $"Pièce {entete.Piece} : déposée au portail DGI le {certifieeLe}, en attente du " +
                "clic qui la certifiera. Elle y est déjà — la renvoyer l'y mettrait deux fois. " +
                "Une fois certifiée au portail, inscrivez-la : debloquer " +
                $"{entete.Piece} --reference … (ou --sans-reference).");
            return EtatPiece.Transmise;
        }

        // Une tentative qui a échoué n'a rien certifié : la pièce redevient
        // candidate. Sans cela, un refus de la plateforme bloquerait à jamais
        // le renvoi de la facture corrigée.
        if (certification.Etat != Fne.EtatFne.Certified)
        {
            rapport.Avertir(
                "TENTATIVE_PRECEDENTE",
                $"Pièce {entete.Piece} : une tentative du {certifieeLe} s'est soldée par " +
                $"« {certification.Etat} »" +
                $"{(certification.Erreur == "" ? "" : $" — {certification.Erreur}")}. " +
                "Rien n'a été certifié : la pièce peut repartir.");

            return facture is not null && !rapport.ContientDesErreurs
                ? EtatPiece.ACertifier
                : EtatPiece.Bloquee;
        }

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
