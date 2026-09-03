using SageFne.Core.Models.Sage;

namespace SageFne.Core.Data;

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
public sealed class DemoSageInvoiceRepository(bool estReel = false)
    : ISageInvoiceRepository, ISageTaxInspector
{
    public const string PieceDemonstration = "1219";

    /// <summary>
    /// Non : ces factures sont fabriquées, et n'ont rien à faire chez la DGI.
    /// </summary>
    /// <remarks>
    /// Le paramètre existe pour les tests d'envoi, qui se servent de ce jeu
    /// comme d'un dossier : ils doivent pouvoir déclarer qu'ils tiennent lieu de
    /// données réelles. Le défaut est <c>false</c>, et le câblage de production
    /// ne passe jamais rien — un test le vérifie sur le conteneur réel, parce
    /// qu'un jour où il vaudrait <c>true</c>, l'agent certifierait à la DGI des
    /// factures inventées.
    /// </remarks>
    public bool EstReel => estReel;

    /// <summary>
    /// Le jeu d'essai ne porte que des ventes : il le dit plutôt que de
    /// fabriquer des achats qu'aucun dossier réel ne confirmerait.
    /// </summary>
    public Task<List<SageDomaineSummary>> GetDomainesAsync(
        CancellationToken cancellation = default) =>
        Task.FromResult(Entetes
            .GroupBy(entete => (entete.Domaine, entete.Type))
            .Select(groupe => new SageDomaineSummary
            {
                Domaine = groupe.Key.Domaine,
                Type = groupe.Key.Type,
                Nombre = groupe.Count(),
                PremiereDate = groupe.Min(e => e.Date),
                DerniereDate = groupe.Max(e => e.Date),
                TotalTTC = groupe.Sum(e => e.TotalTTC),
                Exemple = groupe.OrderByDescending(e => e.Date).First().Piece,
                Tiers = groupe.OrderByDescending(e => e.Date).First().Tiers,
            })
            .OrderBy(d => d.Domaine).ThenBy(d => d.Type)
            .ToList());

    private static readonly SageCustomer[] Clients =
    [
        new()
        {
            CtNum = "4111SITASARL",
            Intitule = "SITA SARL",
            Identifiant = "1432262S",
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
            CtNum = "4011COOP",
            Intitule = "COOPERATIVE DU GRAND OUEST (jeu d'essai)",
            // Un producteur n'a pas de NCC : c'est précisément pourquoi le
            // bordereau d'achat existe, et pourquoi son tableau de paramètres
            // n'en porte aucun.
            Identifiant = "",
            Pays = "COTE D'IVOIRE",
            Telephone = "0709080765",
            Email = "coop@example.test",
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
        Entete("1223", new DateTime(2025, 12, 9), "4111DEMOSA", totalHT: 54000m, totalTTC: 63720m),
        // Comptabilisée : DO_Type 7, DO_DocType 6. C'est le cas le plus fréquent
        // du dossier réel — 913 documents sur 1 008 relevés.
        Entete("1224", new DateTime(2025, 12, 10), "4111DEMOSA", totalHT: 80000m, totalTTC: 94400m,
            type: SageDocumentTypes.FactureComptabilisee),

        // Domaine 1 : les achats. Deux pièces, dont une comptabilisée, comme
        // 6 et 7 côté ventes.
        Achat("AC001", new DateTime(2025, 12, 11), "4011COOP", totalHT: 450000m),
        Achat("AC002", new DateTime(2025, 12, 12), "4011COOP", totalHT: 120000m,
            type: SagePurchaseTypes.FactureComptabilisee),
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

        // Les deux formes de remise, pour que le prix net envoyé se vérifie :
        // 10 % sur la première ligne, 200 F par unité sur la seconde.
        Ligne("1223", 1, "6FF001", "Frites 7 mm - carton", 10m, 5000m, "SAC",
            montantHT: 45000m, montantTTC: 53100m, taxe1: 18m, code1: "TVA",
            remise1: 10m, remise1Type: SageRemise.Pourcentage),
        Ligne("1223", 2, "6FF002", "Frites 9 mm - carton", 5m, 2000m, "SAC",
            montantHT: 9000m, montantTTC: 10620m, taxe1: 18m, code1: "TVA",
            remise1: 200m, remise1Type: SageRemise.Montant),

        // La ligne d'une facture comptabilisée se lit comme les autres.
        Ligne("1224", 1, "13110001", "Tenderloin chain off", 8m, 10000m, "KG",
            montantHT: 80000m, montantTTC: 94400m, taxe1: 18m, code1: "TVA"),

        // Domaine 1 : les achats. Aucune taxe — le bordereau d'achat n'en porte
        // pas, et c'est ce que le chemin d'achat doit savoir traiter.
        LigneAchat("AC001", 1, "CACAO01", "Cacao brut premier choix", 200m, 2000m, "SAC"),
        LigneAchat("AC001", 2, "CAFE01", "Café vert", 50m, 1000m, "SAC"),
        LigneAchat("AC002", 1, "HEVEA01", "Fond de tasse hévéa", 300m, 400m, "KG"),
    ];

    /// <summary>
    /// Documents d'autres types, pour que le diagnostic des types ait quelque
    /// chose à montrer hors base. Ils n'entrent jamais dans un lot à certifier :
    /// seul <see cref="Entetes"/> alimente la lecture des factures.
    /// </summary>
    private static readonly SageDocumentHeader[] AutresDocuments =
    [
        Document(0, "DEV0042", new DateTime(2025, 11, 26), "4111DEMOSA", 118000m),
        Document(0, "DEV0043", new DateTime(2025, 11, 28), "4111SITASARL", 250000m),
        Document(1, "CMD0101", new DateTime(2025, 11, 29), "4111DEMOSA", 118000m),
        Document(3, "BL0500", new DateTime(2025, 12, 2), "4111SITASARL", 498339.625m),
        Document(3, "BL0501", new DateTime(2025, 12, 4), "4111DEMOSA", 152542.14m),
        Document(7, "1180", new DateTime(2025, 10, 31), "4111DEMOSA", 96000m),
        Document(7, "1181", new DateTime(2025, 11, 4), "4111SITASARL", 74400m),
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

    /// <remarks>
    /// Le jeu d'essai imite ce que la requête ramènerait : tous les types du
    /// domaine des ventes, pas seulement le 6. DO_DocType est renseigné, comme
    /// dans un dossier où la colonne existe.
    /// </remarks>
    public Task<List<SageDocumentTypeSummary>> GetDocumentTypesAsync(
        int exemplesParType = 5,
        CancellationToken cancellation = default) =>
        Task.FromResult(Entetes
            .Concat(AutresDocuments)
            .GroupBy(document => document.Type)
            .OrderBy(groupe => groupe.Key)
            .Select(groupe => new SageDocumentTypeSummary
            {
                Type = groupe.Key,
                Nombre = groupe.Count(),
                PremiereDate = groupe.Min(document => document.Date),
                DerniereDate = groupe.Max(document => document.Date),
                TotalTTC = groupe.Sum(document => document.TotalTTC),
                Exemples = groupe
                    .OrderByDescending(document => document.Date)
                    .ThenByDescending(document => document.Piece)
                    .Take(Math.Max(exemplesParType, 0))
                    .Select(document => new SageDocumentSample
                    {
                        Piece = document.Piece,
                        Date = document.Date,
                        Tiers = document.Tiers,
                        TotalTTC = document.TotalTTC,
                        DocType = document.DocType,
                    })
                    .ToList(),
            })
            .ToList());

    public Task<List<SageDocumentHeader>> GetDocumentsByPieceAsync(
        string piece,
        CancellationToken cancellation = default) =>
        Task.FromResult(Entetes
            .Concat(AutresDocuments)
            .Where(document => document.Piece == piece)
            .OrderBy(document => document.Type)
            .ToList());

    /// <remarks>
    /// Le jeu d'essai n'en fabrique aucun : la comptabilisation modifie la ligne
    /// en place, elle n'en crée pas une seconde. C'est l'hypothèse que la vraie
    /// base doit confirmer.
    /// </remarks>
    public Task<List<SageDocumentDuplicate>> GetPiecesMultiTypesAsync(
        CancellationToken cancellation = default) =>
        Task.FromResult(Entetes
            .Concat(AutresDocuments)
            .GroupBy(document => document.Piece)
            .Where(groupe => groupe.Select(document => document.Type).Distinct().Count() > 1)
            .Select(groupe => new SageDocumentDuplicate
            {
                Piece = groupe.Key,
                Nombre = groupe.Count(),
                Types = groupe.Select(document => document.Type).Distinct().Order().ToList(),
                DocTypes = groupe.Select(document => document.DocType).Distinct().Order().ToList(),
            })
            .ToList());

    /// <remarks>
    /// Hors base : le jeu d'essai porte par construction tout ce que le mapping
    /// demande. Seule la vraie base peut répondre à cette question.
    /// </remarks>
    public Task<List<SageColonnesManquantes>> GetColonnesManquantesAsync(
        CancellationToken cancellation = default) =>
        Task.FromResult(new List<SageColonnesManquantes>());

    /// <remarks>Famille « 02 » pour la 13415001, relevée sur le dossier réel.</remarks>
    public Task<Dictionary<string, string>> GetArticleFamiliesAsync(
        IReadOnlyCollection<string> arRefs,
        CancellationToken cancellation = default) =>
        Task.FromResult(Lignes
            .Where(ligne => arRefs.Contains(ligne.ArticleReference))
            .GroupBy(ligne => ligne.ArticleReference, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                groupe => groupe.Key,
                groupe => groupe.Key == "13415001" ? "02" : "01",
                StringComparer.OrdinalIgnoreCase));

    // --- Exploration (jeu d'essai) -----------------------------------------

    /// <remarks>
    /// Hors base, ces lectures ne montrent que la <b>forme</b> de la sortie.
    /// Les colonnes d'un vrai dossier Sage sont bien plus nombreuses, et ce
    /// sont précisément celles-là qu'il faut regarder.
    /// </remarks>
    public Task<List<SageEnregistrement>> LireTableAsync(
        string table,
        int limite = 200,
        CancellationToken cancellation = default) =>
        Task.FromResult(table.Equals("F_TAXE", StringComparison.OrdinalIgnoreCase)
            ? Taxes.Select(taxe => new SageEnregistrement
            {
                Table = "F_TAXE",
                Cle = taxe.Code,
                Champs =
                [
                    new("TA_Code", taxe.Code),
                    new("TA_Intitule", taxe.Intitule),
                    new("TA_Taux", taxe.Taux.ToString("0.##")),
                    new("TA_Type", taxe.Type.ToString()),
                    new("CG_Num", taxe.CompteGeneral),
                    new("TA_Regroup", taxe.Regroupement),
                    new("TA_EdiCode", taxe.EdiCode),
                ],
            }).Take(limite).ToList()
            : []);

    public Task<SageEnregistrement?> LireLigneAsync(
        string table,
        string colonneCle,
        string valeur,
        CancellationToken cancellation = default)
    {
        if (table.Equals("F_COMPTET", StringComparison.OrdinalIgnoreCase))
        {
            var client = Clients.FirstOrDefault(fiche => fiche.CtNum == valeur);
            return Task.FromResult(client is null ? null : new SageEnregistrement
            {
                Table = "F_COMPTET",
                Cle = client.CtNum,
                Champs =
                [
                    new("CT_Num", client.CtNum),
                    new("CT_Intitule", client.Intitule),
                    new("CT_Identifiant", client.Identifiant),
                    new("CT_Pays", client.Pays),
                    new("CT_TypeNIF", client.TypeNif.ToString()),
                ],
            });
        }

        if (table.Equals("F_ARTICLE", StringComparison.OrdinalIgnoreCase))
        {
            var ligne = Lignes.FirstOrDefault(article => article.ArticleReference == valeur);
            return Task.FromResult(ligne is null ? null : new SageEnregistrement
            {
                Table = "F_ARTICLE",
                Cle = ligne.ArticleReference,
                Champs =
                [
                    new("AR_Ref", ligne.ArticleReference),
                    new("AR_Design", ligne.Designation),
                    new("FA_CodeFamille", "(jeu d'essai)"),
                ],
            });
        }

        return Task.FromResult<SageEnregistrement?>(null);
    }

    public Task<List<SageEnregistrement>> LireFiscaliteLignesAsync(
        string piece,
        CancellationToken cancellation = default) =>
        Task.FromResult(Lignes
            .Where(ligne => ligne.Piece == piece)
            .OrderBy(ligne => ligne.Ligne)
            .Select(ligne => new SageEnregistrement
            {
                Table = "F_DOCLIGNE",
                Cle = ligne.Ligne.ToString(),
                Champs =
                [
                    new("DL_Ligne", ligne.Ligne.ToString()),
                    new("AR_Ref", ligne.ArticleReference),
                    new("DL_Design", ligne.Designation),
                    new("DL_Taxe1", ligne.Taxe1.ToString("0.##")),
                    new("DL_CodeTaxe1", ligne.CodeTaxe1),
                    new("DL_TypeTaux1", ligne.TypeTaux1.ToString()),
                    new("DL_TypeTaxe1", ligne.TypeTaxe1.ToString()),
                    new("DL_Taxe2", ligne.Taxe2.ToString("0.##")),
                    new("DL_CodeTaxe2", ligne.CodeTaxe2),
                    new("DL_TypeTaux2", ligne.TypeTaux2.ToString()),
                    new("DL_TypeTaxe2", ligne.TypeTaxe2.ToString()),
                    new("DL_Taxe3", ligne.Taxe3.ToString("0.##")),
                    new("DL_CodeTaxe3", ligne.CodeTaxe3),
                ],
            })
            .ToList());

    private static bool Retenue(InvoiceQuery query, SageDocumentHeader entete) =>
        // Le domaine d'abord, et il manquait : le jeu d'essai rendait ses
        // achats à une lecture de ventes, ce que le dépôt SQL ne fait pas — sa
        // requête porte « where e.DO_Domaine = @domaine ». Une doublure qui se
        // comporte autrement que ce qu'elle double ne prouve rien.
        entete.Domaine == query.Domaine
        && (query.Pieces.Count == 0 || query.Pieces.Contains(entete.Piece))
        && (query.Depuis is null || entete.Date >= query.Depuis)
        && (query.Jusqua is null || entete.Date < query.Jusqua);

    /// <summary>
    /// Un bordereau d'achat, pour que le chemin des achats soit exercé et non
    /// supposé.
    /// </summary>
    /// <remarks>
    /// Domaine 1, types 16 et 17 : ce que <c>domaines</c> a relevé sur le
    /// dossier réel. Sans pièce d'achat dans le jeu d'essai, tout le chemin
    /// d'achat — lecture, mapping sans TVA, contrôles — ne serait éprouvé par
    /// rien du tout.
    /// </remarks>
    private static SageDocumentHeader Achat(
        string piece,
        DateTime date,
        string tiers,
        decimal totalHT,
        short type = SagePurchaseTypes.Facture) => new()
    {
        Domaine = SageDomaines.Achat,
        Type = type,
        DocType = SagePurchaseTypes.Facture,
        Piece = piece,
        Date = date,
        Tiers = tiers,
        TotalHT = totalHT,
        // Un bordereau d'achat ne porte pas de TVA : le TTC vaut le HT.
        TotalTTC = totalHT,
        NetAPayer = totalHT,
        Statut = 0,
    };

    private static SageDocumentHeader Entete(
        string piece,
        DateTime date,
        string tiers,
        decimal totalHT,
        decimal totalTTC,
        short type = SageDocumentTypes.Facture) => new()
    {
        Domaine = 0,
        Type = type,
        // La comptabilisation change DO_Type, jamais DO_DocType.
        DocType = SageDocumentTypes.Facture,
        Piece = piece,
        Date = date,
        Tiers = tiers,
        TotalHT = totalHT,
        TotalTTC = totalTTC,
        NetAPayer = totalTTC,
        Statut = 0,
    };

    private static SageDocumentHeader Document(
        short type,
        string piece,
        DateTime date,
        string tiers,
        decimal totalTTC) => new()
    {
        Domaine = 0,
        Type = type,
        DocType = type == SageDocumentTypes.FactureComptabilisee ? SageDocumentTypes.Facture : type,
        Piece = piece,
        Date = date,
        Tiers = tiers,
        TotalTTC = totalTTC,
        NetAPayer = totalTTC,
    };

    /// <summary>Une ligne de bordereau d'achat : aucune taxe, par nature.</summary>
    private static SageDocumentLine LigneAchat(
        string piece,
        int rang,
        string article,
        string designation,
        decimal quantite,
        decimal prixUnitaire,
        string unite) => new()
    {
        Domaine = SageDomaines.Achat,
        Type = Entetes.First(entete => entete.Piece == piece).Type,
        Piece = piece,
        Ligne = rang,
        CtNum = Entetes.First(entete => entete.Piece == piece).Tiers,
        Date = Entetes.First(entete => entete.Piece == piece).Date,
        ArticleReference = article,
        Designation = designation,
        Quantite = quantite,
        PrixUnitaire = prixUnitaire,
        Unite = unite,
        MontantHT = quantite * prixUnitaire,
        MontantTTC = quantite * prixUnitaire,
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
        string code2 = "",
        decimal remise1 = 0m,
        short remise1Type = SageRemise.Pourcentage) => new()
    {
        Domaine = 0,
        Type = Entetes.First(entete => entete.Piece == piece).Type,
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
        Remise1 = remise1,
        Remise1Type = remise1Type,
    };
}
