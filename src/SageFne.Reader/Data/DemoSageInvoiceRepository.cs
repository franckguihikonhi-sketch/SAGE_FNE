using SageFne.Reader.Models.Sage;

namespace SageFne.Reader.Data;

/// <summary>
/// Jeu d'essai hors base, calqué sur la pièce 1219 du dossier.
/// </summary>
/// <remarks>
/// Il sert au dry run tant que la chaîne de connexion n'est pas renseignée :
/// le mapping et les contrôles s'exécutent alors pour de vrai, sur des valeurs
/// relevées dans Sage. Dès que la connexion est configurée, c'est
/// <see cref="SageInvoiceRepository"/> qui prend la main et ce jeu n'est plus
/// utilisé.
/// </remarks>
public sealed class DemoSageInvoiceRepository : ISageInvoiceRepository
{
    public const string PieceDemonstration = "1219";

    private static readonly SageDocumentHeader Entete = new()
    {
        Domaine = 0,
        Type = 6,
        Piece = PieceDemonstration,
        Date = new DateTime(2025, 12, 3),
        Tiers = "4111SITASARL",
        // Le dossier laisse DO_TotalHT à 0 sur une partie des documents :
        // le jeu d'essai reproduit ce cas, qui est le plus piégeux.
        TotalHT = 0m,
        TotalTTC = 498339.625m,
        NetAPayer = 498339.625m,
        Statut = 0,
    };

    private static readonly SageCustomer Client = new()
    {
        CtNum = "4111SITASARL",
        Intitule = "SITA SARL",
        Identifiant = "14322625",
        Pays = "COTE D'IVOIRE",
    };

    private static readonly SageDocumentLine[] Lignes =
    [
        new()
        {
            Domaine = 0,
            Type = 6,
            Piece = PieceDemonstration,
            Ligne = 1,
            Date = new DateTime(2025, 12, 3),
            CtNum = "4111SITASARL",
            ArticleReference = "13415001",
            Designation = "Queue De Boeuf PV - Friboi",
            Quantite = 196.390000m,
            PrixUnitaire = 2500.000000m,
            Unite = "KG",
            Taxe1 = 0m,
            CodeTaxe1 = "",
            Taxe2 = 1.500000m,
            CodeTaxe2 = "AIRSI",
            PrixUnitaireTTC = 2537.500000m,
            MontantHT = 490975.000000m,
            MontantTTC = 498339.625000m,
            DocType = 6,
        },
    ];

    private static readonly SageTaxDefinition[] Taxes =
    [
        new() { Code = "AIRSI", Intitule = "AIRSI", Taux = 1.5m },
        new() { Code = "TVA", Intitule = "TVA/VENTE", Taux = 9m },
        new() { Code = "TVA0", Intitule = "TVA/ACHAT", Taux = 18m },
    ];

    public Task<SageDocumentHeader?> GetInvoiceAsync(string piece, CancellationToken cancellation = default) =>
        Task.FromResult(piece == PieceDemonstration ? Entete : null);

    public Task<List<SageDocumentLine>> GetInvoiceLinesAsync(string piece, CancellationToken cancellation = default) =>
        Task.FromResult(piece == PieceDemonstration ? Lignes.ToList() : []);

    public Task<SageCustomer?> GetCustomerAsync(string ctNum, CancellationToken cancellation = default) =>
        Task.FromResult(ctNum == Client.CtNum ? Client : null);

    public Task<List<SageTaxDefinition>> GetTaxesAsync(CancellationToken cancellation = default) =>
        Task.FromResult(Taxes.ToList());
}
