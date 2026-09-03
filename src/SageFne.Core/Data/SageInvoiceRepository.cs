using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SageFne.Core.Models.Sage;

namespace SageFne.Core.Data;

/// <summary>
/// Lecture de la base Sage sur SQL Server.
/// </summary>
/// <remarks>
/// Toutes les requêtes sont des SELECT paramétrés, passés par
/// <see cref="ReadOnlyGuard"/> avant exécution. Les critères venant de
/// l'extérieur ne sont jamais concaténés dans le texte : seuls des *noms* de
/// paramètres sont engendrés, jamais des valeurs.
/// </remarks>
public sealed class SageInvoiceRepository(string connectionString, ILogger<SageInvoiceRepository> logger)
    : ISageInvoiceRepository, ISageTaxInspector
{
    /// <summary>Oui : les documents viennent du dossier Sage, en lecture seule.</summary>
    public bool EstReel => true;

    /// <summary>Domaine 0 = ventes.</summary>
    private const short DomaineVente = 0;

    /// <summary>
    /// Les deux états d'une facture : 6 avant comptabilisation, 7 après. Sage
    /// fait passer DO_Type de l'un à l'autre sur la même ligne — c'est le même
    /// document, et le lot doit voir les deux.
    /// </summary>
    private const short TypeFacture = SageDocumentTypes.Facture;
    private const short TypeComptabilisee = SageDocumentTypes.FactureComptabilisee;

    /// <summary>Le filtre de type, écrit une fois pour toutes les requêtes.</summary>
    private const string FiltreTypesFacture =
        "DO_Type in (@typeFacture, @typeComptabilisee)";

    /// <summary>
    /// SQL Server plafonne à 2 100 paramètres par commande : les listes sont
    /// découpées bien en deçà, une lecture par tranche.
    /// </summary>
    private const int TailleTranche = 500;

    /// <summary>
    /// Ce qu'on aimerait lire dans F_DOCENTETE. Ce qui n'existe pas dans le
    /// dossier est retiré du select : voir <see cref="ColonnesTable"/>.
    /// </summary>
    private static readonly string[] SouhaiteesEntete =
    [
        "DO_Domaine", "DO_Type", "DO_DocType", "DO_Piece", "DO_Date", "DO_Tiers",
        "DO_TotalHT", "DO_TotalTTC", "DO_NetAPayer", "DO_Statut",
    ];

    /// <summary>Sans elles, aucune pièce n'est identifiable.</summary>
    private static readonly string[] IndispensablesEntete =
        ["DO_Domaine", "DO_Type", "DO_Piece", "DO_Date", "DO_Tiers"];

    /// <summary>
    /// Ce qu'on aimerait lire dans F_DOCLIGNE.
    /// </summary>
    /// <remarks>
    /// Aucun équivalent de DO_DocType n'y figure : le type d'origine se lit sur
    /// l'entête, F_DOCENTETE.DO_DocType, et nulle part ailleurs. La ligne se
    /// rattache à son entête par DO_Domaine, DO_Piece et DO_Type, qui existent
    /// bien dans les deux tables.
    /// </remarks>
    internal static readonly string[] SouhaiteesLignes =
    [
        "DO_Domaine", "DO_Type", "DO_Piece", "DO_Date", "DL_Ligne", "CT_Num", "DO_Ref",
        "AR_Ref", "DL_Design", "DL_Qte", "DL_PrixUnitaire",
        "DL_Remise01REM_Valeur", "DL_Remise01REM_Type",
        "DL_Remise02REM_Valeur", "DL_Remise02REM_Type",
        "DL_Remise03REM_Valeur", "DL_Remise03REM_Type",
        "DL_Taxe1", "DL_TypeTaux1", "DL_TypeTaxe1", "DL_CodeTaxe1",
        "DL_Taxe2", "DL_TypeTaux2", "DL_TypeTaxe2", "DL_CodeTaxe2",
        "DL_Taxe3", "DL_CodeTaxe3",
        "EU_Enumere", "EU_Qte", "DL_TTC", "DL_PUTTC",
        "DL_MontantHT", "DL_MontantTTC",
    ];

    /// <summary>Sans elles, la ligne ne peut ni être rattachée ni être chiffrée.</summary>
    private static readonly string[] IndispensablesLignes =
        ["DO_Domaine", "DO_Type", "DO_Piece", "DL_Ligne", "DL_Design", "DL_Qte", "DL_PrixUnitaire"];

    private static readonly string[] SouhaiteesClient =
    [
        "CT_Num", "CT_Intitule", "CT_Identifiant", "CT_Adresse", "CT_Complement",
        "CT_CodePostal", "CT_Ville", "CT_Pays", "CT_Telephone", "CT_EMail", "CT_TypeNIF",
    ];

    private static readonly string[] IndispensablesClient = ["CT_Num", "CT_Intitule"];

    public const string TableEntete = "F_DOCENTETE";
    public const string TableLignes = "F_DOCLIGNE";
    public const string TableClient = "F_COMPTET";

    /// <summary>
    /// Le catalogue n'est lu qu'une fois par table et par exécution : il ne
    /// change pas pendant un lot.
    /// </summary>
    private readonly Dictionary<string, ColonnesTable> _catalogue = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _verrouCatalogue = new(1, 1);

    // --- Une pièce ---------------------------------------------------------

    public async Task<SageDocumentHeader?> GetInvoiceAsync(string piece, CancellationToken cancellation = default)
    {
        // Une pièce comptabilisée et sa version d'avant portent le même numéro :
        // c'est l'état le plus avancé qui décrit le document aujourd'hui.
        await using var connexion = await OuvrirAsync(cancellation);
        var colonnes = await ColonnesEnteteAsync(connexion, cancellation);

        var sql = $"""
            select top (1)
            {colonnes.Selection("e", SouhaiteesEntete)}
            from F_DOCENTETE e
            where e.DO_Domaine = @domaine
              and e.{FiltreTypesFacture}
              and e.DO_Piece = @piece
            order by e.DO_Type desc
            """;

        await using var commande = Commande(connexion, sql);
        Ajouter(commande, "@domaine", DomaineVente);
        AjouterTypes(commande);
        Ajouter(commande, "@piece", piece);

        await using var lecteur = await commande.ExecuteReaderAsync(cancellation);
        return await lecteur.ReadAsync(cancellation) ? LireEntete(lecteur, colonnes) : null;
    }

    /// <remarks>
    /// Passe par la lecture de lot : une seule règle de rattachement des
    /// lignes à leur entête, pour une pièce comme pour cinquante.
    /// </remarks>
    public Task<List<SageDocumentLine>> GetInvoiceLinesAsync(
        string piece,
        CancellationToken cancellation = default) =>
        GetLinesAsync(InvoiceQuery.Piece(piece), cancellation);

    public async Task<SageCustomer?> GetCustomerAsync(string ctNum, CancellationToken cancellation = default)
    {
        await using var connexion = await OuvrirAsync(cancellation);
        var colonnes = await ColonnesClientAsync(connexion, cancellation);

        var sql = $"""
            select top (1)
            {colonnes.Selection("c", SouhaiteesClient)}
            from F_COMPTET c
            where c.CT_Num = @ctNum
            """;

        await using var commande = Commande(connexion, sql);
        Ajouter(commande, "@ctNum", ctNum);

        await using var lecteur = await commande.ExecuteReaderAsync(cancellation);
        return await lecteur.ReadAsync(cancellation) ? LireClient(lecteur, colonnes) : null;
    }

    // --- Un lot ------------------------------------------------------------

    public async Task<List<SageDocumentHeader>> GetInvoicesAsync(
        InvoiceQuery query,
        CancellationToken cancellation = default)
    {
        var entetes = new List<SageDocumentHeader>();

        await using var connexion = await OuvrirAsync(cancellation);
        var colonnes = await ColonnesEnteteAsync(connexion, cancellation);

        foreach (var tranche in Tranches(query))
        {
            var criteres = new CritereSql("e");
            var sql = $"""
                select top (@limite)
                {colonnes.Selection("e", SouhaiteesEntete)}
                from F_DOCENTETE e
                where e.DO_Domaine = @domaine
                  and e.{FiltreTypesFacture}
                {criteres.Where(tranche)}
                order by e.DO_Date, e.DO_Piece
                """;

            await using var commande = Commande(connexion, sql);
            Ajouter(commande, "@domaine", query.Domaine);
            AjouterTypes(commande, query.Domaine);
            Ajouter(commande, "@limite", query.Limite);
            criteres.Appliquer(commande, tranche);

            await using var lecteur = await commande.ExecuteReaderAsync(cancellation);
            while (await lecteur.ReadAsync(cancellation)) entetes.Add(LireEntete(lecteur, colonnes));
        }

        logger.LogDebug("{Nombre} entête(s) lue(s) pour {Critere}.", entetes.Count, query.Describe());
        return entetes.Take(query.Limite).ToList();
    }

    public async Task<List<SageDocumentLine>> GetLinesAsync(
        InvoiceQuery query,
        CancellationToken cancellation = default)
    {
        var lignes = new List<SageDocumentLine>();

        await using var connexion = await OuvrirAsync(cancellation);
        var colonnes = await ColonnesLignesAsync(connexion, cancellation);

        foreach (var tranche in Tranches(query))
        {
            var criteres = new CritereSql("e");
            await using var commande = Commande(
                connexion,
                SqlLignes(criteres, tranche, colonnes.Selection("l", SouhaiteesLignes)));
            Ajouter(commande, "@domaine", query.Domaine);
            AjouterTypes(commande, query.Domaine);
            criteres.Appliquer(commande, tranche);

            lignes.AddRange(await LireLignesAsync(commande, colonnes, cancellation));
        }

        return lignes;
    }

    public async Task<List<SageCustomer>> GetCustomersAsync(
        IReadOnlyCollection<string> ctNums,
        CancellationToken cancellation = default)
    {
        var clients = new List<SageCustomer>();
        var distincts = ctNums.Where(nom => !string.IsNullOrWhiteSpace(nom)).Distinct().ToList();

        await using var connexion = await OuvrirAsync(cancellation);
        var colonnes = await ColonnesClientAsync(connexion, cancellation);

        foreach (var tranche in distincts.Chunk(TailleTranche))
        {
            var noms = tranche.Select((_, rang) => $"@ct{rang}").ToArray();
            var sql = $"""
                select
                {colonnes.Selection("c", SouhaiteesClient)}
                from F_COMPTET c
                where c.CT_Num in ({string.Join(", ", noms)})
                """;

            await using var commande = Commande(connexion, sql);
            for (var rang = 0; rang < tranche.Length; rang++) Ajouter(commande, noms[rang], tranche[rang]);

            await using var lecteur = await commande.ExecuteReaderAsync(cancellation);
            while (await lecteur.ReadAsync(cancellation)) clients.Add(LireClient(lecteur, colonnes));
        }

        return clients;
    }

    public async Task<List<SageTaxDefinition>> GetTaxesAsync(CancellationToken cancellation = default)
    {
        const string sql = """
            select TA_Code, TA_Intitule, TA_Taux, TA_Type, CG_Num, TA_Regroup, TA_EdiCode
            from F_TAXE
            order by TA_Code
            """;

        await using var connexion = await OuvrirAsync(cancellation);
        await using var commande = Commande(connexion, sql);

        var taxes = new List<SageTaxDefinition>();
        await using var lecteur = await commande.ExecuteReaderAsync(cancellation);
        while (await lecteur.ReadAsync(cancellation))
        {
            taxes.Add(new SageTaxDefinition
            {
                Code = lecteur.Text("TA_Code"),
                Intitule = lecteur.Text("TA_Intitule"),
                Taux = lecteur.Amount("TA_Taux"),
                Type = lecteur.Small("TA_Type"),
                CompteGeneral = lecteur.Text("CG_Num"),
                Regroupement = lecteur.Text("TA_Regroup"),
                EdiCode = lecteur.Text("TA_EdiCode"),
            });
        }

        return taxes;
    }

    // --- Diagnostic des types de documents ---------------------------------

    /// <remarks>
    /// Deux lectures : le dénombrement par type, puis les derniers exemplaires
    /// de chaque type. La colonne DO_DocType n'existe pas dans toutes les
    /// versions du dossier ; on demande d'abord au catalogue si elle est là,
    /// plutôt que de faire échouer la requête pour l'apprendre.
    ///
    /// Aucun filtre sur DO_Type ici : c'est précisément la question posée.
    /// </remarks>
    public async Task<List<SageDocumentTypeSummary>> GetDocumentTypesAsync(
        int exemplesParType = 5,
        CancellationToken cancellation = default)
    {
        await using var connexion = await OuvrirAsync(cancellation);

        var avecDocType = await ColonneExisteAsync(connexion, "F_DOCENTETE", "DO_DocType", cancellation);
        var totaux = await LireTotauxAsync(connexion, cancellation);
        Dictionary<short, IReadOnlyList<SageDocumentSample>> exemples = exemplesParType > 0
            ? await LireExemplesAsync(connexion, avecDocType, exemplesParType, cancellation)
            : new();

        logger.LogDebug(
            "{Nombre} type(s) de document dans le domaine des ventes ; DO_DocType {Presence}.",
            totaux.Count,
            avecDocType ? "présente" : "absente");

        return totaux
            .Select(total => new SageDocumentTypeSummary
            {
                Type = total.Type,
                Nombre = total.Nombre,
                PremiereDate = total.PremiereDate,
                DerniereDate = total.DerniereDate,
                TotalTTC = total.TotalTTC,
                Exemples = exemples.TryGetValue(total.Type, out var trouves) ? trouves : [],
            })
            .ToList();
    }

    /// <summary>
    /// Tous les domaines et types, avec un exemplaire de chacun.
    /// </summary>
    /// <remarks>
    /// Aucun filtre sur DO_Domaine : c'est tout l'objet. Le middleware ne lit
    /// que le domaine 0, et cette requête est le seul endroit d'où l'on peut
    /// voir ce que le dossier contient par ailleurs.
    ///
    /// L'exemplaire est pris par row_number plutôt que par une sous-requête par
    /// groupe : une seule lecture, et l'ordre décroissant donne le document le
    /// plus récent, celui que l'exploitant reconnaîtra le mieux.
    /// </remarks>
    internal const string SqlDomaines = """
        select
          e.DO_Domaine as DO_Domaine,
          e.DO_Type as DO_Type,
          count(*) as Nombre,
          min(e.DO_Date) as PremiereDate,
          max(e.DO_Date) as DerniereDate,
          sum(e.DO_TotalTTC) as TotalTTC,
          max(rtrim(e.DO_Piece) + '|' + rtrim(e.DO_Tiers)) as Exemplaire
        from F_DOCENTETE e
        group by e.DO_Domaine, e.DO_Type
        order by e.DO_Domaine, e.DO_Type
        """;

    public async Task<List<SageDomaineSummary>> GetDomainesAsync(
        CancellationToken cancellation = default)
    {
        await using var connexion = await OuvrirAsync(cancellation);
        await using var commande = Commande(connexion, SqlDomaines);

        var domaines = new List<SageDomaineSummary>();
        await using var lecteur = await commande.ExecuteReaderAsync(cancellation);
        while (await lecteur.ReadAsync(cancellation))
        {
            domaines.Add(new SageDomaineSummary
            {
                Domaine = lecteur.Small("DO_Domaine"),
                Type = lecteur.Small("DO_Type"),
                Nombre = lecteur.Whole("Nombre"),
                PremiereDate = lecteur.MomentOrNull("PremiereDate"),
                DerniereDate = lecteur.MomentOrNull("DerniereDate"),
                TotalTTC = lecteur.Amount("TotalTTC"),
                Exemple = Avant(lecteur.Text("Exemplaire")),
                Tiers = Apres(lecteur.Text("Exemplaire")),
            });
        }

        logger.LogDebug("{Nombre} couple(s) domaine/type dans F_DOCENTETE.", domaines.Count);
        return domaines;
    }

    /// <summary>
    /// La pièce et le compte tiers viennent concaténés d'une seule agrégation.
    /// </summary>
    /// <remarks>
    /// Deux <c>max()</c> séparés auraient pu prendre la pièce d'un document et
    /// le compte d'un autre — un exemplaire qui n'existe pas, présenté comme
    /// s'il existait. Concaténés, ils viennent forcément de la même ligne.
    ///
    /// Et non une fenêtre <c>row_number()</c> dans une CTE, qui aurait donné
    /// l'exemplaire le plus récent : <see cref="ReadOnlyGuard"/> exige que le
    /// texte commence par <c>select</c>, et refuse donc <c>with</c>. La requête
    /// aurait échoué à l'exécution — l'affaiblir pour la faire passer aurait
    /// été prendre le problème par le mauvais bout.
    /// </remarks>
    private static string Avant(string concatene)
    {
        var separateur = concatene.IndexOf('|');
        return separateur < 0 ? concatene : concatene[..separateur];
    }

    private static string Apres(string concatene)
    {
        var separateur = concatene.IndexOf('|');
        return separateur < 0 ? "" : concatene[(separateur + 1)..];
    }

    internal const string SqlTypesDocuments = """
        select
          e.DO_Type as DO_Type,
          count(*) as Nombre,
          min(e.DO_Date) as PremiereDate,
          max(e.DO_Date) as DerniereDate,
          sum(e.DO_TotalTTC) as TotalTTC
        from F_DOCENTETE e
        where e.DO_Domaine = @domaine
        group by e.DO_Type
        order by e.DO_Type
        """;

    /// <summary>
    /// Les derniers documents de chaque type, numérotés par type puis filtrés :
    /// une seule lecture au lieu d'une par type.
    /// </summary>
    internal static string SqlExemplesTypes(bool avecDocType)
    {
        var docType = avecDocType ? ", e.DO_DocType" : "";
        var reprise = avecDocType ? ", DO_DocType" : "";

        return $"""
            select DO_Type, DO_Piece, DO_Date, DO_Tiers, DO_TotalTTC{reprise}
            from (
              select
                e.DO_Type, e.DO_Piece, e.DO_Date, e.DO_Tiers, e.DO_TotalTTC{docType},
                row_number() over (partition by e.DO_Type order by e.DO_Date desc, e.DO_Piece desc) as Rang
              from F_DOCENTETE e
              where e.DO_Domaine = @domaine
            ) as Derniers
            where Rang <= @exemples
            order by DO_Type, Rang
            """;
    }

    /// <summary>Le catalogue de la base, consulté en lecture comme le reste.</summary>
    internal const string SqlColonneExiste = """
        select count(*) as Presente
        from INFORMATION_SCHEMA.COLUMNS
        where TABLE_NAME = @table and COLUMN_NAME = @colonne
        """;

    private async Task<bool> ColonneExisteAsync(
        SqlConnection connexion,
        string table,
        string colonne,
        CancellationToken cancellation)
    {
        await using var commande = Commande(connexion, SqlColonneExiste);
        Ajouter(commande, "@table", table);
        Ajouter(commande, "@colonne", colonne);

        await using var lecteur = await commande.ExecuteReaderAsync(cancellation);
        return await lecteur.ReadAsync(cancellation) && lecteur.Whole("Presente") > 0;
    }

    private async Task<List<SageDocumentTypeSummary>> LireTotauxAsync(
        SqlConnection connexion,
        CancellationToken cancellation)
    {
        await using var commande = Commande(connexion, SqlTypesDocuments);
        Ajouter(commande, "@domaine", DomaineVente);

        var totaux = new List<SageDocumentTypeSummary>();
        await using var lecteur = await commande.ExecuteReaderAsync(cancellation);
        while (await lecteur.ReadAsync(cancellation))
        {
            totaux.Add(new SageDocumentTypeSummary
            {
                Type = lecteur.Small("DO_Type"),
                Nombre = lecteur.Whole("Nombre"),
                PremiereDate = lecteur.MomentOrNull("PremiereDate"),
                DerniereDate = lecteur.MomentOrNull("DerniereDate"),
                TotalTTC = lecteur.Amount("TotalTTC"),
            });
        }

        return totaux;
    }

    private async Task<Dictionary<short, IReadOnlyList<SageDocumentSample>>> LireExemplesAsync(
        SqlConnection connexion,
        bool avecDocType,
        int exemplesParType,
        CancellationToken cancellation)
    {
        await using var commande = Commande(connexion, SqlExemplesTypes(avecDocType));
        Ajouter(commande, "@domaine", DomaineVente);
        Ajouter(commande, "@exemples", exemplesParType);

        var parType = new Dictionary<short, List<SageDocumentSample>>();
        await using var lecteur = await commande.ExecuteReaderAsync(cancellation);
        while (await lecteur.ReadAsync(cancellation))
        {
            var type = lecteur.Small("DO_Type");
            if (!parType.TryGetValue(type, out var liste)) parType[type] = liste = [];
            liste.Add(new SageDocumentSample
            {
                Piece = lecteur.Text("DO_Piece"),
                Date = lecteur.Moment("DO_Date"),
                Tiers = lecteur.Text("DO_Tiers"),
                TotalTTC = lecteur.Amount("DO_TotalTTC"),
                DocType = avecDocType ? lecteur.SmallOrNull("DO_DocType") : null,
            });
        }

        return parType.ToDictionary(
            entree => entree.Key,
            entree => (IReadOnlyList<SageDocumentSample>)entree.Value);
    }

    /// <remarks>Aucun filtre de type : c'est tout l'intérêt de cette lecture.</remarks>
    public async Task<List<SageDocumentHeader>> GetDocumentsByPieceAsync(
        string piece,
        CancellationToken cancellation = default)
    {
        await using var connexion = await OuvrirAsync(cancellation);
        var colonnes = await ColonnesEnteteAsync(connexion, cancellation);

        var sql = $"""
            select
            {colonnes.Selection("e", SouhaiteesEntete)}
            from F_DOCENTETE e
            where e.DO_Domaine = @domaine
              and e.DO_Piece = @piece
            order by e.DO_Type
            """;

        await using var commande = Commande(connexion, sql);
        Ajouter(commande, "@domaine", DomaineVente);
        Ajouter(commande, "@piece", piece);

        var entetes = new List<SageDocumentHeader>();
        await using var lecteur = await commande.ExecuteReaderAsync(cancellation);
        while (await lecteur.ReadAsync(cancellation)) entetes.Add(LireEntete(lecteur, colonnes));
        return entetes;
    }

    internal const string SqlPiecesMultiTypes = """
        select
          e.DO_Piece as DO_Piece,
          count(*) as Nombre,
          count(distinct e.DO_Type) as Types,
          min(e.DO_Type) as TypeMin,
          max(e.DO_Type) as TypeMax,
          min(e.DO_DocType) as DocTypeMin,
          max(e.DO_DocType) as DocTypeMax
        from F_DOCENTETE e
        where e.DO_Domaine = @domaine
        group by e.DO_Piece
        having count(distinct e.DO_Type) > 1
        order by e.DO_Piece
        """;

    public async Task<List<SageDocumentDuplicate>> GetPiecesMultiTypesAsync(
        CancellationToken cancellation = default)
    {
        await using var connexion = await OuvrirAsync(cancellation);
        await using var commande = Commande(connexion, SqlPiecesMultiTypes);
        Ajouter(commande, "@domaine", DomaineVente);

        var doublons = new List<SageDocumentDuplicate>();
        await using var lecteur = await commande.ExecuteReaderAsync(cancellation);
        while (await lecteur.ReadAsync(cancellation))
        {
            // Le groupement ne rend que les bornes : deux types distincts sont
            // exactement décrits par leur minimum et leur maximum, et au-delà
            // de deux, ces bornes suffisent à donner l'alerte.
            var min = lecteur.Small("TypeMin");
            var max = lecteur.Small("TypeMax");
            var docMin = lecteur.Small("DocTypeMin");
            var docMax = lecteur.Small("DocTypeMax");

            doublons.Add(new SageDocumentDuplicate
            {
                Piece = lecteur.Text("DO_Piece"),
                Nombre = lecteur.Whole("Nombre"),
                Types = min == max ? [min] : [min, max],
                DocTypes = docMin == docMax ? [docMin] : [docMin, docMax],
            });
        }

        return doublons;
    }

    // --- Le catalogue ------------------------------------------------------

    /// <summary>
    /// Les colonnes d'une table, demandées au catalogue de SQL Server.
    /// </summary>
    /// <remarks>
    /// <c>sys.columns</c> et <c>sys.tables</c> sont lisibles par n'importe quel
    /// compte ayant accès à la base : un <c>db_datareader</c> suffit. C'est une
    /// lecture comme les autres, passée par le même garde-fou.
    /// </remarks>
    internal const string SqlColonnesDeTable = """
        select c.name as Colonne
        from sys.columns c
        inner join sys.tables t on t.object_id = c.object_id
        where t.name = @table
        """;

    private async Task<ColonnesTable> ColonnesAsync(
        SqlConnection connexion,
        string table,
        CancellationToken cancellation)
    {
        await _verrouCatalogue.WaitAsync(cancellation);
        try
        {
            if (_catalogue.TryGetValue(table, out var connues)) return connues;

            await using var commande = Commande(connexion, SqlColonnesDeTable);
            Ajouter(commande, "@table", table);

            var noms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var lecteur = await commande.ExecuteReaderAsync(cancellation))
            {
                while (await lecteur.ReadAsync(cancellation)) noms.Add(lecteur.Text("Colonne"));
            }

            if (noms.Count == 0)
            {
                throw new InvalidOperationException(
                    $"La table {table} est introuvable dans cette base. " +
                    "La chaîne de connexion pointe-t-elle bien sur le dossier commercial Sage ?");
            }

            var colonnes = new ColonnesTable(table, noms);
            _catalogue[table] = colonnes;
            logger.LogDebug("{Table} : {Nombre} colonnes au catalogue.", table, noms.Count);
            return colonnes;
        }
        finally
        {
            _verrouCatalogue.Release();
        }
    }

    /// <summary>
    /// Ce que le dossier ne porte pas, parmi ce que la lecture aimerait avoir.
    /// </summary>
    /// <remarks>
    /// Diagnostic : une colonne absente ne fait plus échouer la lecture, mais
    /// elle prive le mapping d'une information. Autant que ça se voie.
    /// </remarks>
    public async Task<List<SageColonnesManquantes>> GetColonnesManquantesAsync(
        CancellationToken cancellation = default)
    {
        await using var connexion = await OuvrirAsync(cancellation);

        var releve = new List<SageColonnesManquantes>();
        foreach (var (table, souhaitees, indispensables) in new[]
                 {
                     (TableEntete, SouhaiteesEntete, IndispensablesEntete),
                     (TableLignes, SouhaiteesLignes, IndispensablesLignes),
                     (TableClient, SouhaiteesClient, IndispensablesClient),
                 })
        {
            var colonnes = await ColonnesAsync(connexion, table, cancellation);
            releve.Add(new SageColonnesManquantes
            {
                Table = table,
                Total = colonnes.Presentes.Count,
                Demandees = souhaitees.Length,
                Absentes = colonnes.Absentes(souhaitees),
                AbsentesIndispensables = colonnes.Absentes(indispensables),
            });
        }

        return releve;
    }

    /// <remarks>
    /// Les deux colonnes sont vérifiées au catalogue : un dossier sans
    /// FA_CodeFamille rend simplement une table vide, et la classification par
    /// famille ne joue pas.
    /// </remarks>
    public async Task<Dictionary<string, string>> GetArticleFamiliesAsync(
        IReadOnlyCollection<string> arRefs,
        CancellationToken cancellation = default)
    {
        var familles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var distincts = arRefs.Where(reference => !string.IsNullOrWhiteSpace(reference)).Distinct().ToList();
        if (distincts.Count == 0) return familles;

        await using var connexion = await OuvrirAsync(cancellation);
        var colonnes = await ColonnesAsync(connexion, "F_ARTICLE", cancellation);
        if (!colonnes.A("AR_Ref") || !colonnes.A("FA_CodeFamille"))
        {
            logger.LogDebug("F_ARTICLE sans AR_Ref ou FA_CodeFamille : classification par famille inactive.");
            return familles;
        }

        foreach (var tranche in distincts.Chunk(TailleTranche))
        {
            var noms = tranche.Select((_, rang) => $"@ar{rang}").ToArray();
            var sql = $"""
                select a.AR_Ref, a.FA_CodeFamille
                from F_ARTICLE a
                where a.AR_Ref in ({string.Join(", ", noms)})
                """;

            await using var commande = Commande(connexion, sql);
            for (var rang = 0; rang < tranche.Length; rang++) Ajouter(commande, noms[rang], tranche[rang]);

            await using var lecteur = await commande.ExecuteReaderAsync(cancellation);
            while (await lecteur.ReadAsync(cancellation))
            {
                familles[lecteur.Text("AR_Ref")] = lecteur.Text("FA_CodeFamille");
            }
        }

        return familles;
    }

    // --- Exploration -------------------------------------------------------

    /// <remarks>
    /// Toutes les colonnes, parce qu'on cherche justement celle dont on ignore
    /// le nom. Le nom de la table est contrôlé deux fois : sa forme, puis son
    /// existence au catalogue.
    /// </remarks>
    public async Task<List<SageEnregistrement>> LireTableAsync(
        string table,
        int limite = 200,
        CancellationToken cancellation = default)
    {
        var nom = IdentifiantSql.Verifier(table);

        await using var connexion = await OuvrirAsync(cancellation);
        var colonnes = await ColonnesAsync(connexion, nom, cancellation);
        var retenues = Ordonner(colonnes.Presentes);

        var sql = $"""
            select top (@limite)
            {string.Join(", ", retenues.Select(colonne => $"t.{colonne}"))}
            from {nom} t
            """;

        await using var commande = Commande(connexion, sql);
        Ajouter(commande, "@limite", limite);

        return await LireEnregistrementsAsync(commande, nom, retenues, CleNaturelle(retenues), cancellation);
    }

    public async Task<SageEnregistrement?> LireLigneAsync(
        string table,
        string colonneCle,
        string valeur,
        CancellationToken cancellation = default)
    {
        var nom = IdentifiantSql.Verifier(table);
        var cle = IdentifiantSql.Verifier(colonneCle);

        await using var connexion = await OuvrirAsync(cancellation);
        var colonnes = await ColonnesAsync(connexion, nom, cancellation);

        if (!colonnes.A(cle))
        {
            throw new InvalidOperationException($"La table {nom} ne porte pas de colonne {cle}.");
        }

        var retenues = Ordonner(colonnes.Presentes);
        var sql = $"""
            select top (1)
            {string.Join(", ", retenues.Select(colonne => $"t.{colonne}"))}
            from {nom} t
            where t.{cle} = @valeur
            """;

        await using var commande = Commande(connexion, sql);
        Ajouter(commande, "@valeur", valeur);

        var trouves = await LireEnregistrementsAsync(commande, nom, retenues, cle, cancellation);
        return trouves.FirstOrDefault();
    }

    /// <remarks>
    /// Les colonnes dont le nom évoque une taxe, plus de quoi reconnaître la
    /// ligne. Rien n'est interprété : c'est le brut de F_DOCLIGNE.
    /// </remarks>
    public async Task<List<SageEnregistrement>> LireFiscaliteLignesAsync(
        string piece,
        CancellationToken cancellation = default)
    {
        await using var connexion = await OuvrirAsync(cancellation);
        var colonnes = await ColonnesAsync(connexion, TableLignes, cancellation);

        var reperes = new[] { "DL_Ligne", "AR_Ref", "DL_Design", "DL_Qte", "DL_PrixUnitaire" };
        var fiscales = colonnes.Presentes
            .Where(colonne =>
                colonne.Contains("Taxe", StringComparison.OrdinalIgnoreCase)
                || colonne.Contains("TVA", StringComparison.OrdinalIgnoreCase)
                || colonne.Contains("TypeTaux", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);

        var retenues = reperes.Where(colonnes.A).Concat(fiscales).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var sql = $"""
            select
            {string.Join(", ", retenues.Select(colonne => $"l.{colonne}"))}
            from F_DOCLIGNE l
            where l.DO_Domaine = @domaine
              and l.{FiltreTypesFacture}
              and l.DO_Piece = @piece
            order by l.DL_Ligne
            """;

        await using var commande = Commande(connexion, sql);
        Ajouter(commande, "@domaine", DomaineVente);
        AjouterTypes(commande);
        Ajouter(commande, "@piece", piece);

        return await LireEnregistrementsAsync(
            commande, TableLignes, retenues, colonnes.A("DL_Ligne") ? "DL_Ligne" : retenues[0], cancellation);
    }

    /// <summary>
    /// Les colonnes fonctionnelles d'abord, les « cb… » de réplication ensuite.
    /// </summary>
    private static List<string> Ordonner(IEnumerable<string> colonnes) => colonnes
        .OrderBy(colonne => colonne.StartsWith("cb", StringComparison.Ordinal))
        .ThenBy(colonne => colonne, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// De quoi nommer une fiche à l'affichage.
    /// </summary>
    /// <remarks>
    /// Prendre la première colonne venue donnait « System.Byte[] » comme titre,
    /// l'ordre alphabétique plaçant cbCG_Num en tête. Un code, une référence ou
    /// un numéro nomme bien mieux une fiche.
    /// </remarks>
    private static string CleNaturelle(IReadOnlyList<string> colonnes)
    {
        var fonctionnelles = colonnes
            .Where(colonne => !colonne.StartsWith("cb", StringComparison.Ordinal))
            .ToList();

        foreach (var suffixe in new[] { "_Code", "_Ref", "_Num", "_No" })
        {
            var trouvee = fonctionnelles.FirstOrDefault(colonne =>
                colonne.EndsWith(suffixe, StringComparison.OrdinalIgnoreCase));
            if (trouvee is not null) return trouvee;
        }

        return fonctionnelles.FirstOrDefault() ?? colonnes[0];
    }

    private static async Task<List<SageEnregistrement>> LireEnregistrementsAsync(
        SqlCommand commande,
        string table,
        IReadOnlyList<string> colonnes,
        string colonneCle,
        CancellationToken cancellation)
    {
        var enregistrements = new List<SageEnregistrement>();
        await using var lecteur = await commande.ExecuteReaderAsync(cancellation);

        while (await lecteur.ReadAsync(cancellation))
        {
            var champs = colonnes
                .Select(colonne => new SageChamp(colonne, lecteur.Text(colonne)))
                .ToList();

            enregistrements.Add(new SageEnregistrement
            {
                Table = table,
                Cle = champs.FirstOrDefault(champ =>
                    string.Equals(champ.Colonne, colonneCle, StringComparison.OrdinalIgnoreCase)).Valeur ?? "",
                Champs = champs,
            });
        }

        return enregistrements
            .OrderBy(enregistrement => enregistrement.Cle, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // --- Plomberie ---------------------------------------------------------

    /// <summary>
    /// Requête des lignes d'un lot.
    /// </summary>
    /// <remarks>
    /// Les lignes se rattachent à leur entête par le domaine, le numéro de
    /// pièce <b>et la famille de type</b> : filtrer sur le seul numéro
    /// ramènerait aussi les lignes d'un document d'un autre type portant le même
    /// numéro — un bon de livraison 1219 en même temps que la facture 1219.
    ///
    /// La famille, et non l'égalité stricte des DO_Type : la comptabilisation
    /// fait passer l'entête de 6 à 7, et exiger <c>e.DO_Type = l.DO_Type</c>
    /// ramènerait zéro ligne si les deux tables n'étaient pas exactement en
    /// phase. Les deux côtés restent bornés à {6, 7}, ce qui écarte toujours le
    /// bon de livraison.
    /// </remarks>
    /// <param name="colonnes">
    /// Liste de sélection déjà réduite à ce que F_DOCLIGNE porte réellement.
    /// Par défaut, tout ce que la lecture souhaite : les tests s'en servent
    /// pour vérifier le texte sans toucher à une base.
    /// </param>
    internal static string SqlLignes(CritereSql criteres, InvoiceQuery query, string? colonnes = null) => $"""
        select
        {colonnes ?? string.Join(", ", SouhaiteesLignes.Select(colonne => $"l.{colonne}"))}
        from F_DOCLIGNE l
        where l.DO_Domaine = @domaine
          and l.{FiltreTypesFacture}
          and exists (
                select 1
                from F_DOCENTETE e
                where e.DO_Domaine = l.DO_Domaine
                  and e.DO_Piece = l.DO_Piece
                  and e.{FiltreTypesFacture}
                {criteres.Where(query)}
          )
        order by l.DO_Piece, l.DL_Ligne
        """;

    /// <summary>
    /// Découpe une liste de pièces en tranches lisibles d'une seule commande.
    /// Sans liste de pièces, une seule passe sur le critère de dates.
    /// </summary>
    private static IEnumerable<InvoiceQuery> Tranches(InvoiceQuery query)
    {
        if (query.Pieces.Count <= TailleTranche) return [query];
        return query.Pieces.Chunk(TailleTranche).Select(tranche => query with { Pieces = tranche });
    }

    private static async Task<List<SageDocumentLine>> LireLignesAsync(
        SqlCommand commande,
        ColonnesTable colonnes,
        CancellationToken cancellation)
    {
        var lignes = new List<SageDocumentLine>();
        await using var lecteur = await commande.ExecuteReaderAsync(cancellation);
        while (await lecteur.ReadAsync(cancellation)) lignes.Add(LireLigne(lecteur, colonnes));
        return lignes;
    }

    private Task<ColonnesTable> ColonnesEnteteAsync(SqlConnection connexion, CancellationToken cancellation) =>
        ColonnesVerifieesAsync(connexion, TableEntete, IndispensablesEntete, cancellation);

    private Task<ColonnesTable> ColonnesLignesAsync(SqlConnection connexion, CancellationToken cancellation) =>
        ColonnesVerifieesAsync(connexion, TableLignes, IndispensablesLignes, cancellation);

    private Task<ColonnesTable> ColonnesClientAsync(SqlConnection connexion, CancellationToken cancellation) =>
        ColonnesVerifieesAsync(connexion, TableClient, IndispensablesClient, cancellation);

    private async Task<ColonnesTable> ColonnesVerifieesAsync(
        SqlConnection connexion,
        string table,
        string[] indispensables,
        CancellationToken cancellation)
    {
        var colonnes = await ColonnesAsync(connexion, table, cancellation);
        colonnes.Exiger(indispensables);
        return colonnes;
    }

    private static SageDocumentHeader LireEntete(SqlDataReader lecteur, ColonnesTable colonnes) => new()
    {
        Domaine = lecteur.Small(colonnes, "DO_Domaine"),
        Type = lecteur.Small(colonnes, "DO_Type"),
        DocType = lecteur.Small(colonnes, "DO_DocType"),
        Piece = lecteur.Text(colonnes, "DO_Piece"),
        Date = lecteur.Moment(colonnes, "DO_Date"),
        Tiers = lecteur.Text(colonnes, "DO_Tiers"),
        TotalHT = lecteur.Amount(colonnes, "DO_TotalHT"),
        TotalTTC = lecteur.Amount(colonnes, "DO_TotalTTC"),
        NetAPayer = lecteur.Amount(colonnes, "DO_NetAPayer"),
        Statut = lecteur.Small(colonnes, "DO_Statut"),
    };

    private static SageDocumentLine LireLigne(SqlDataReader lecteur, ColonnesTable colonnes) => new()
    {
        Domaine = lecteur.Small(colonnes, "DO_Domaine"),
        Type = lecteur.Small(colonnes, "DO_Type"),
        Piece = lecteur.Text(colonnes, "DO_Piece"),
        Ligne = lecteur.Whole(colonnes, "DL_Ligne"),
        Date = lecteur.Moment(colonnes, "DO_Date"),
        CtNum = lecteur.Text(colonnes, "CT_Num"),
        DocumentReference = lecteur.Text(colonnes, "DO_Ref"),
        ArticleReference = lecteur.Text(colonnes, "AR_Ref"),
        Designation = lecteur.Text(colonnes, "DL_Design"),
        Quantite = lecteur.Amount(colonnes, "DL_Qte"),
        PrixUnitaire = lecteur.Amount(colonnes, "DL_PrixUnitaire"),
        Unite = lecteur.Text(colonnes, "EU_Enumere"),
        QuantiteUnite = lecteur.Amount(colonnes, "EU_Qte"),
        Remise1 = lecteur.Amount(colonnes, "DL_Remise01REM_Valeur"),
        Remise1Type = lecteur.Small(colonnes, "DL_Remise01REM_Type"),
        Remise2 = lecteur.Amount(colonnes, "DL_Remise02REM_Valeur"),
        Remise2Type = lecteur.Small(colonnes, "DL_Remise02REM_Type"),
        Remise3 = lecteur.Amount(colonnes, "DL_Remise03REM_Valeur"),
        Remise3Type = lecteur.Small(colonnes, "DL_Remise03REM_Type"),
        Taxe1 = lecteur.Amount(colonnes, "DL_Taxe1"),
        CodeTaxe1 = lecteur.Text(colonnes, "DL_CodeTaxe1"),
        TypeTaux1 = lecteur.Small(colonnes, "DL_TypeTaux1"),
        TypeTaxe1 = lecteur.Small(colonnes, "DL_TypeTaxe1"),
        Taxe2 = lecteur.Amount(colonnes, "DL_Taxe2"),
        CodeTaxe2 = lecteur.Text(colonnes, "DL_CodeTaxe2"),
        TypeTaux2 = lecteur.Small(colonnes, "DL_TypeTaux2"),
        TypeTaxe2 = lecteur.Small(colonnes, "DL_TypeTaxe2"),
        Taxe3 = lecteur.Amount(colonnes, "DL_Taxe3"),
        CodeTaxe3 = lecteur.Text(colonnes, "DL_CodeTaxe3"),
        MontantHT = lecteur.Amount(colonnes, "DL_MontantHT"),
        MontantTTC = lecteur.Amount(colonnes, "DL_MontantTTC"),
        PrixUnitaireTTC = lecteur.Amount(colonnes, "DL_PUTTC"),
        EstTTC = lecteur.Flag(colonnes, "DL_TTC"),
    };

    private static SageCustomer LireClient(SqlDataReader lecteur, ColonnesTable colonnes) => new()
    {
        CtNum = lecteur.Text(colonnes, "CT_Num"),
        Intitule = lecteur.Text(colonnes, "CT_Intitule"),
        Identifiant = lecteur.Text(colonnes, "CT_Identifiant"),
        Adresse = lecteur.Text(colonnes, "CT_Adresse"),
        Complement = lecteur.Text(colonnes, "CT_Complement"),
        CodePostal = lecteur.Text(colonnes, "CT_CodePostal"),
        Ville = lecteur.Text(colonnes, "CT_Ville"),
        Pays = lecteur.Text(colonnes, "CT_Pays"),
        Telephone = lecteur.Text(colonnes, "CT_Telephone"),
        Email = lecteur.Text(colonnes, "CT_EMail"),
        TypeNif = lecteur.Small(colonnes, "CT_TypeNIF"),
    };

    private async Task<SqlConnection> OuvrirAsync(CancellationToken cancellation)
    {
        var connexion = new SqlConnection(connectionString);
        await connexion.OpenAsync(cancellation);
        return connexion;
    }

    private static SqlCommand Commande(SqlConnection connexion, string sql) =>
        new(ReadOnlyGuard.Verify(sql), connexion) { CommandType = CommandType.Text };

    private static void Ajouter(SqlCommand commande, string nom, string valeur) =>
        commande.Parameters.Add(nom, SqlDbType.VarChar, 50).Value = valeur;

    private static void Ajouter(SqlCommand commande, string nom, short valeur) =>
        commande.Parameters.Add(nom, SqlDbType.SmallInt).Value = valeur;

    private static void Ajouter(SqlCommand commande, string nom, int valeur) =>
        commande.Parameters.Add(nom, SqlDbType.Int).Value = valeur;

    /// <summary>Les deux états d'une facture, liés d'un seul geste.</summary>
    private static void AjouterTypes(SqlCommand commande) =>
        AjouterTypes(commande, SageDomaines.Vente);

    /// <summary>
    /// Les deux états d'une facture, dans le domaine demandé.
    /// </summary>
    /// <remarks>
    /// Vente : 6 et 7. Achat : 16 et 17. Dans les deux cas, deux états d'un même
    /// document que la comptabilisation fait passer de l'un à l'autre — relevés
    /// sur le dossier réel, pas devinés.
    ///
    /// Un domaine inconnu retombe sur la vente plutôt que de lire n'importe
    /// quoi : c'est le domaine que le middleware a toujours lu, et le seul dont
    /// les règles fiscales sont écrites.
    /// </remarks>
    private static void AjouterTypes(SqlCommand commande, short domaine)
    {
        var (facture, comptabilisee) = domaine == SageDomaines.Achat
            ? (SagePurchaseTypes.Facture, SagePurchaseTypes.FactureComptabilisee)
            : (TypeFacture, TypeComptabilisee);

        Ajouter(commande, "@typeFacture", facture);
        Ajouter(commande, "@typeComptabilisee", comptabilisee);
    }
}
