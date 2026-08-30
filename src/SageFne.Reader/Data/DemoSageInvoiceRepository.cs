using SageFne.Reader.Models.Sage;

namespace SageFne.Reader.Data;

/// <summary>
/// Jeu d'essai hors base : la pièce 1219 relevée dans le dossier, et trois
/// pièces bâties autour d'elle pour couvrir les cas du lot.
/// </summary>
/// <remarks>
/// Il sert au dry run tant que la chaîne de connexion n'est pas renseignée :
/// le mapping et les contrôles s'exécutent alors pour de vrai. Dès que la
/// connexion est configurée, <see cref="SageInvoiceRepository"/> prend la main
/// et ce jeu n'est plus utilisé.
///
/// La 1219 est réelle. Les trois autres sont inventées, et le disent : elles
/// existent pour montrer une TVA à 18 %, une TVA à 9 % avec prélèvement, et un
/// client sans NCC — le cas qui doit bloquer sans arrêter le lot.
/// </remarks>
public sealed class DemoSageInvoiceRepository : ISageInvoiceRepository
{
    public const string PieceDemonstration = "1219";

    private static readonly SageCustomer[] Clients =
    [
        new()
        {
            CtNum = "4111SITASARL",
            Intitule = "SITA SARL",
            Identifiant = "14322625",
            Pays = "COTE D'IVOIRE",
        },
        new()
        {
            CtNum = "4111DEMOSA",
            Intitule = "DEMO SA (jeu d'essai)",
            Identifiant = "9988776C",
            Pays = "COTE D'IVOIRE",
            Telephone = "0700000000",
            Email = "demo@example.test",
        },
        new()
        {
            CtNum = "4111SANSNCC",
            Intitule = "CLIENT SANS NCC (jeu d'essai)",
            Identifiant = "",
            Pays = "COTE D'IVOIRE",
        },
    ];

    private static readonly SageDocumentHeader[] Entetes =
    [
        Entete("1219", new DateTime(2025, 12, 3), "4111SITASARL", totalHT: 0m, totalTTC: 498339.625m),
        Entete("1220", new DateTime(2025, 12, 4), "4111DEMOSA", totalHT: 129273m, totalTTC: 152542.14m),
        Entete("1221", new DateTime(2025, 12, 5), "4111DEMOSA", totalHT: 200000m, totalTTC: 221000m),
        Entete("1222", new DateTime(2025, 12, 8), "4111SANSNCC", totalHT: 50000m, totalTTC: 50750m),
    ];

    private static readonly SageDocumentLine[] Lignes =
    [
        // Pièce réelle : exonérée de TVA, soumise à l'AIRSI.
        Ligne("1219", 1, "13415001", "Queue De Boeuf PV - Friboi", 196.39m, 2500m, "KG",
            montantHT: 490975m, montantTTC: 498339.625m, taxe2: 1.5m, code2: "AIRSI"),

        // Taux normal, deux lignes, pour vérifier le regroupement.
        Ligne("1220", 1, "6FF001", "Frites 7 mm - carton", 120m, 1077.2763m, "SAC",
            montantHT: 129273.16m, montantTTC: 152542.33m, taxe1: 18m, code1: "TVA"),
        Ligne("1220", 2, "6FF002", "Frites 9 mm - carton", 0.01m, 1000m, "SAC",
            montantHT: 10m, montantTTC: 11.80m, taxe1: 18m, code1: "TVA"),

        // Taux réduit et prélèvement sur la même ligne.
        Ligne("1221", 1, "13110001", "Tenderloin chain off", 20m, 10000m, "KG",
            montantHT: 200000m, montantTTC: 221000m, taxe1: 9m, code1: "TVA", taxe2: 1.5m, code2: "AIRSI"),

        // Client sans NCC : la pièce doit être écartée, pas le lot.
        Ligne("1222", 1, "25MK033", "Maquereau 12 kg", 5m, 10000m, "CN",
            montantHT: 50000m, montantTTC: 50750m, taxe2: 1.5m, code2: "AIRSI"),
    ];

    private static readonly SageTaxDefinition[] Taxes =
    [
        new() { Code = "AIRSI", Intitule = "AIRSI", Taux = 1.5m },
        new() { Code = "TVA", Intitule = "TVA/VENTE", Taux = 9m },
        new() { Code = "TVA0", Intitule = "TVA/ACHAT", Taux = 18m },
    ];

    public Task<SageDocumentHeader?> GetInvoiceAsync(string piece, CancellationToken cancellation = default) =>
        Task.FromResult(Entetes.FirstOrDefault(entete => entete.Piece == piece));

    /// <summary>Même chemin que le lot, comme dans le dépôt SQL.</summary>
    public Task<List<SageDocumentLine>> GetInvoiceLinesAsync(string piece, CancellationToken cancellation = default) =>
        GetLinesAsync(InvoiceQuery.Piece(piece), cancellation);

    public Task<SageCustomer?> GetCustomerAsync(string ctNum, CancellationToken cancellation = default) =>
        Task.FromResult(Clients.FirstOrDefault(client => client.CtNum == ctNum));

    public Task<List<SageDocumentHeader>> GetInvoicesAsync(
        InvoiceQuery query,
        CancellationToken cancellation = default) =>
        Task.FromResult(Entetes
            .Where(entete => Retenue(query, entete))
            .OrderBy(entete => entete.Date)
            .ThenBy(entete => entete.Piece)
            .Take(query.Limite)
            .ToList());

    public Task<List<SageDocumentLine>> GetLinesAsync(
        InvoiceQuery query,
        CancellationToken cancellation = default)
    {
        var pieces = Entetes.Where(entete => Retenue(query, entete)).Select(entete => entete.Piece).ToHashSet();
        return Task.FromResult(Lignes
            .Where(ligne => pieces.Contains(ligne.Piece))
            .OrderBy(ligne => ligne.Piece)
            .ThenBy(ligne => ligne.Ligne)
            .ToList());
    }

    public Task<List<SageCustomer>> GetCustomersAsync(
        IReadOnlyCollection<string> ctNums,
        CancellationToken cancellation = default) =>
        Task.FromResult(Clients.Where(client => ctNums.Contains(client.CtNum)).ToList());

    public Task<List<SageTaxDefinition>> GetTaxesAsync(CancellationToken cancellation = default) =>
        Task.FromResult(Taxes.ToList());

    private static bool Retenue(InvoiceQuery query, SageDocumentHeader entete) =>
        (query.Pieces.Count == 0 || query.Pieces.Contains(entete.Piece))
        && (query.Depuis is null || entete.Date >= query.Depuis)
        && (query.Jusqua is null || entete.Date < query.Jusqua);

    private static SageDocumentHeader Entete(
        string piece,
        DateTime date,
        string tiers,
        decimal totalHT,
        decimal totalTTC) => new()
    {
        Domaine = 0,
        Type = 6,
        Piece = piece,
        Date = date,
        Tiers = tiers,
        TotalHT = totalHT,
        TotalTTC = totalTTC,
        NetAPayer = totalTTC,
        Statut = 0,
    };

    private static SageDocumentLine Ligne(
        string piece,
        int rang,
        string article,
        string designation,
        decimal quantite,
        decimal prixUnitaire,
        string unite,
        decimal montantHT,
        decimal montantTTC,
        decimal taxe1 = 0m,
        string code1 = "",
        decimal taxe2 = 0m,
        string code2 = "") => new()
    {
        Domaine = 0,
        Type = 6,
        Piece = piece,
        Ligne = rang,
        CtNum = Entetes.First(entete => entete.Piece == piece).Tiers,
        Date = Entetes.First(entete => entete.Piece == piece).Date,
        ArticleReference = article,
        Designation = designation,
        Quantite = quantite,
        PrixUnitaire = prixUnitaire,
        Unite = unite,
        MontantHT = montantHT,
        MontantTTC = montantTTC,
        Taxe1 = taxe1,
        CodeTaxe1 = code1,
        Taxe2 = taxe2,
        CodeTaxe2 = code2,
        DocType = 6,
    };
}
