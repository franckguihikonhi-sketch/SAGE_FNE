using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SageFne.Reader.Models.Sage;

namespace SageFne.Reader.Data;

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
    : ISageInvoiceRepository
{
    /// <summary>Domaine 0 = ventes.</summary>
    private const short DomaineVente = 0;

    /// <summary>Type 6 = facture. Les autres types restent à confirmer.</summary>
    private const short TypeFacture = 6;

    /// <summary>
    /// SQL Server plafonne à 2 100 paramètres par commande : les listes sont
    /// découpées bien en deçà, une lecture par tranche.
    /// </summary>
    private const int TailleTranche = 500;

    private const string ColonnesEntete = """
        e.DO_Domaine, e.DO_Type, e.DO_Piece, e.DO_Date, e.DO_Tiers,
        e.DO_TotalHT, e.DO_TotalTTC, e.DO_NetAPayer, e.DO_Statut
        """;

    private const string ColonnesLignes = """
        l.DO_Domaine, l.DO_Type, l.DO_Piece, l.DO_Date, l.DL_Ligne, l.CT_Num, l.DO_Ref,
        l.AR_Ref, l.DL_Design, l.DL_Qte, l.DL_PrixUnitaire,
        l.DL_Remise01REM_Valeur, l.DL_Remise02REM_Valeur, l.DL_Remise03REM_Valeur,
        l.DL_Taxe1, l.DL_TypeTaux1, l.DL_TypeTaxe1, l.DL_CodeTaxe1,
        l.DL_Taxe2, l.DL_TypeTaux2, l.DL_TypeTaxe2, l.DL_CodeTaxe2,
        l.DL_Taxe3, l.DL_CodeTaxe3,
        l.EU_Enumere, l.EU_Qte, l.DL_TTC, l.DL_PUTTC,
        l.DL_MontantHT, l.DL_MontantTTC, l.DL_DocType
        """;

    private const string ColonnesClient = """
        CT_Num, CT_Intitule, CT_Identifiant, CT_Adresse, CT_Complement,
        CT_CodePostal, CT_Ville, CT_Pays, CT_Telephone, CT_EMail, CT_TypeNIF
        """;

    // --- Une pièce ---------------------------------------------------------

    public async Task<SageDocumentHeader?> GetInvoiceAsync(string piece, CancellationToken cancellation = default)
    {
        var sql = $"""
            select top (1)
            {ColonnesEntete}
            from F_DOCENTETE e
            where e.DO_Domaine = @domaine
              and e.DO_Type = @type
              and e.DO_Piece = @piece
            """;

        await using var connexion = await OuvrirAsync(cancellation);
        await using var commande = Commande(connexion, sql);
        Ajouter(commande, "@domaine", DomaineVente);
        Ajouter(commande, "@type", TypeFacture);
        Ajouter(commande, "@piece", piece);

        await using var lecteur = await commande.ExecuteReaderAsync(cancellation);
        return await lecteur.ReadAsync(cancellation) ? LireEntete(lecteur) : null;
    }

    public async Task<List<SageDocumentLine>> GetInvoiceLinesAsync(
        string piece,
        CancellationToken cancellation = default)
    {
        var sql = $"""
            select
            {ColonnesLignes}
            from F_DOCLIGNE l
            where l.DO_Domaine = @domaine
              and l.DO_Piece = @piece
            order by l.DL_Ligne
            """;

        await using var connexion = await OuvrirAsync(cancellation);
        await using var commande = Commande(connexion, sql);
        Ajouter(commande, "@domaine", DomaineVente);
        Ajouter(commande, "@piece", piece);

        return await LireLignesAsync(commande, cancellation);
    }

    public async Task<SageCustomer?> GetCustomerAsync(string ctNum, CancellationToken cancellation = default)
    {
        var sql = $"""
            select top (1)
            {ColonnesClient}
            from F_COMPTET
            where CT_Num = @ctNum
            """;

        await using var connexion = await OuvrirAsync(cancellation);
        await using var commande = Commande(connexion, sql);
        Ajouter(commande, "@ctNum", ctNum);

        await using var lecteur = await commande.ExecuteReaderAsync(cancellation);
        return await lecteur.ReadAsync(cancellation) ? LireClient(lecteur) : null;
    }

    // --- Un lot ------------------------------------------------------------

    public async Task<List<SageDocumentHeader>> GetInvoicesAsync(
        InvoiceQuery query,
        CancellationToken cancellation = default)
    {
        var entetes = new List<SageDocumentHeader>();

        foreach (var tranche in Tranches(query))
        {
            var criteres = new CritereSql("e");
            var sql = $"""
                select top (@limite)
                {ColonnesEntete}
                from F_DOCENTETE e
                where e.DO_Domaine = @domaine
                  and e.DO_Type = @type
                {criteres.Where(tranche)}
                order by e.DO_Date, e.DO_Piece
                """;

            await using var connexion = await OuvrirAsync(cancellation);
            await using var commande = Commande(connexion, sql);
            Ajouter(commande, "@domaine", DomaineVente);
            Ajouter(commande, "@type", TypeFacture);
            Ajouter(commande, "@limite", query.Limite);
            criteres.Appliquer(commande, tranche);

            await using var lecteur = await commande.ExecuteReaderAsync(cancellation);
            while (await lecteur.ReadAsync(cancellation)) entetes.Add(LireEntete(lecteur));
        }

        logger.LogDebug("{Nombre} entête(s) lue(s) pour {Critere}.", entetes.Count, query.Describe());
        return entetes.Take(query.Limite).ToList();
    }

    public async Task<List<SageDocumentLine>> GetLinesAsync(
        InvoiceQuery query,
        CancellationToken cancellation = default)
    {
        var lignes = new List<SageDocumentLine>();

        foreach (var tranche in Tranches(query))
        {
            // Les lignes se filtrent par leur entête : filtrer sur le seul
            // numéro de pièce ramènerait aussi les lignes d'un document d'un
            // autre type portant le même numéro.
            var criteres = new CritereSql("e");
            var sql = $"""
                select
                {ColonnesLignes}
                from F_DOCLIGNE l
                where l.DO_Domaine = @domaine
                  and exists (
                        select 1
                        from F_DOCENTETE e
                        where e.DO_Domaine = l.DO_Domaine
                          and e.DO_Type = l.DO_Type
                          and e.DO_Piece = l.DO_Piece
                          and e.DO_Type = @type
                        {criteres.Where(tranche)}
                  )
                order by l.DO_Piece, l.DL_Ligne
                """;

            await using var connexion = await OuvrirAsync(cancellation);
            await using var commande = Commande(connexion, sql);
            Ajouter(commande, "@domaine", DomaineVente);
            Ajouter(commande, "@type", TypeFacture);
            criteres.Appliquer(commande, tranche);

            lignes.AddRange(await LireLignesAsync(commande, cancellation));
        }

        return lignes;
    }

    public async Task<List<SageCustomer>> GetCustomersAsync(
        IReadOnlyCollection<string> ctNums,
        CancellationToken cancellation = default)
    {
        var clients = new List<SageCustomer>();
        var distincts = ctNums.Where(nom => !string.IsNullOrWhiteSpace(nom)).Distinct().ToList();

        foreach (var tranche in distincts.Chunk(TailleTranche))
        {
            var noms = tranche.Select((_, rang) => $"@ct{rang}").ToArray();
            var sql = $"""
                select
                {ColonnesClient}
                from F_COMPTET
                where CT_Num in ({string.Join(", ", noms)})
                """;

            await using var connexion = await OuvrirAsync(cancellation);
            await using var commande = Commande(connexion, sql);
            for (var rang = 0; rang < tranche.Length; rang++) Ajouter(commande, noms[rang], tranche[rang]);

            await using var lecteur = await commande.ExecuteReaderAsync(cancellation);
            while (await lecteur.ReadAsync(cancellation)) clients.Add(LireClient(lecteur));
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

    // --- Plomberie ---------------------------------------------------------

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
        CancellationToken cancellation)
    {
        var lignes = new List<SageDocumentLine>();
        await using var lecteur = await commande.ExecuteReaderAsync(cancellation);
        while (await lecteur.ReadAsync(cancellation)) lignes.Add(LireLigne(lecteur));
        return lignes;
    }

    private static SageDocumentHeader LireEntete(SqlDataReader lecteur) => new()
    {
        Domaine = lecteur.Small("DO_Domaine"),
        Type = lecteur.Small("DO_Type"),
        Piece = lecteur.Text("DO_Piece"),
        Date = lecteur.Moment("DO_Date"),
        Tiers = lecteur.Text("DO_Tiers"),
        TotalHT = lecteur.Amount("DO_TotalHT"),
        TotalTTC = lecteur.Amount("DO_TotalTTC"),
        NetAPayer = lecteur.Amount("DO_NetAPayer"),
        Statut = lecteur.Small("DO_Statut"),
    };

    private static SageDocumentLine LireLigne(SqlDataReader lecteur) => new()
    {
        Domaine = lecteur.Small("DO_Domaine"),
        Type = lecteur.Small("DO_Type"),
        Piece = lecteur.Text("DO_Piece"),
        Ligne = lecteur.Whole("DL_Ligne"),
        Date = lecteur.Moment("DO_Date"),
        CtNum = lecteur.Text("CT_Num"),
        DocumentReference = lecteur.Text("DO_Ref"),
        ArticleReference = lecteur.Text("AR_Ref"),
        Designation = lecteur.Text("DL_Design"),
        Quantite = lecteur.Amount("DL_Qte"),
        PrixUnitaire = lecteur.Amount("DL_PrixUnitaire"),
        Unite = lecteur.Text("EU_Enumere"),
        QuantiteUnite = lecteur.Amount("EU_Qte"),
        Remise1 = lecteur.Amount("DL_Remise01REM_Valeur"),
        Remise2 = lecteur.Amount("DL_Remise02REM_Valeur"),
        Remise3 = lecteur.Amount("DL_Remise03REM_Valeur"),
        Taxe1 = lecteur.Amount("DL_Taxe1"),
        CodeTaxe1 = lecteur.Text("DL_CodeTaxe1"),
        TypeTaux1 = lecteur.Small("DL_TypeTaux1"),
        TypeTaxe1 = lecteur.Small("DL_TypeTaxe1"),
        Taxe2 = lecteur.Amount("DL_Taxe2"),
        CodeTaxe2 = lecteur.Text("DL_CodeTaxe2"),
        TypeTaux2 = lecteur.Small("DL_TypeTaux2"),
        TypeTaxe2 = lecteur.Small("DL_TypeTaxe2"),
        Taxe3 = lecteur.Amount("DL_Taxe3"),
        CodeTaxe3 = lecteur.Text("DL_CodeTaxe3"),
        MontantHT = lecteur.Amount("DL_MontantHT"),
        MontantTTC = lecteur.Amount("DL_MontantTTC"),
        PrixUnitaireTTC = lecteur.Amount("DL_PUTTC"),
        EstTTC = lecteur.Flag("DL_TTC"),
        DocType = lecteur.Small("DL_DocType"),
    };

    private static SageCustomer LireClient(SqlDataReader lecteur) => new()
    {
        CtNum = lecteur.Text("CT_Num"),
        Intitule = lecteur.Text("CT_Intitule"),
        Identifiant = lecteur.Text("CT_Identifiant"),
        Adresse = lecteur.Text("CT_Adresse"),
        Complement = lecteur.Text("CT_Complement"),
        CodePostal = lecteur.Text("CT_CodePostal"),
        Ville = lecteur.Text("CT_Ville"),
        Pays = lecteur.Text("CT_Pays"),
        Telephone = lecteur.Text("CT_Telephone"),
        Email = lecteur.Text("CT_EMail"),
        TypeNif = lecteur.Small("CT_TypeNIF"),
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
}
