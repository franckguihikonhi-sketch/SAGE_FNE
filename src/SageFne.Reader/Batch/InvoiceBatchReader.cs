using Microsoft.Extensions.Options;
using SageFne.Reader.Configuration;
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

        var conversions = new List<InvoiceConversion>(entetes.Count);
        foreach (var entete in entetes)
        {
            conversions.Add(Convertir(entete, parPiece, clients));
        }

        return new InvoiceBatch { Conversions = conversions, Constats = constats.Constats };
    }

    private InvoiceConversion Convertir(
        SageDocumentHeader entete,
        IReadOnlyDictionary<string, List<SageDocumentLine>> parPiece,
        IReadOnlyDictionary<string, SageCustomer> clients)
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

        return new InvoiceConversion
        {
            Header = entete,
            Customer = client,
            Lines = lignes,
            Invoice = facture,
            Report = rapport,
        };
    }
}
