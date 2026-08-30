using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SageFne.Reader.Models.Sage;

namespace SageFne.Reader.Data;

/// <summary>
/// Lecture de la base Sage sur SQL Server.
/// </summary>
/// <remarks>
/// Toutes les requêtes sont des SELECT paramétrés, passés par
/// <see cref="ReadOnlyGuard"/> avant exécution. La pièce arrive de l'extérieur :
/// elle ne doit jamais être concaténée dans le texte de la requête.
/// </remarks>
public sealed class SageInvoiceRepository(string connectionString, ILogger<SageInvoiceRepository> logger)
    : ISageInvoiceRepository
{
    /// <summary>Domaine 0 = ventes.</summary>
    private const short DomaineVente = 0;

    /// <summary>Type 6 = facture. Les autres types restent à confirmer.</summary>
    private const short TypeFacture = 6;

    private const string RequeteEntete = """
        select top (1)
            DO_Domaine, DO_Type, DO_Piece, DO_Date, DO_Tiers,
            DO_TotalHT, DO_TotalTTC, DO_NetAPayer, DO_Statut
        from F_DOCENTETE
        where DO_Domaine = @domaine
          and DO_Type = @type
          and DO_Piece = @piece
        """;

    private const string RequeteLignes = """
        select
            DO_Domaine, DO_Type, DO_Piece, DO_Date, DL_Ligne, CT_Num, DO_Ref,
            AR_Ref, DL_Design, DL_Qte, DL_PrixUnitaire,
            DL_Remise01REM_Valeur, DL_Remise02REM_Valeur, DL_Remise03REM_Valeur,
            DL_Taxe1, DL_TypeTaux1, DL_TypeTaxe1, DL_CodeTaxe1,
            DL_Taxe2, DL_TypeTaux2, DL_TypeTaxe2, DL_CodeTaxe2,
            DL_Taxe3, DL_CodeTaxe3,
            EU_Enumere, EU_Qte, DL_TTC, DL_PUTTC,
            DL_MontantHT, DL_MontantTTC, DL_DocType
        from F_DOCLIGNE
        where DO_Domaine = @domaine
          and DO_Piece = @piece
        order by DL_Ligne
        """;

    private const string RequeteClient = """
        select top (1)
            CT_Num, CT_Intitule, CT_Identifiant, CT_Adresse, CT_Complement,
            CT_CodePostal, CT_Ville, CT_Pays, CT_Telephone, CT_EMail, CT_TypeNIF
        from F_COMPTET
        where CT_Num = @ctNum
        """;

    private const string RequeteTaxes = """
        select TA_Code, TA_Intitule, TA_Taux, TA_Type, CG_Num, TA_Regroup, TA_EdiCode
        from F_TAXE
        order by TA_Code
        """;

    public async Task<SageDocumentHeader?> GetInvoiceAsync(string piece, CancellationToken cancellation = default)
    {
        await using var connexion = await OuvrirAsync(cancellation);
        await using var commande = Commande(connexion, RequeteEntete);
        commande.Parameters.Add("@domaine", System.Data.SqlDbType.SmallInt).Value = DomaineVente;
        commande.Parameters.Add("@type", System.Data.SqlDbType.SmallInt).Value = TypeFacture;
        commande.Parameters.Add("@piece", System.Data.SqlDbType.VarChar, 50).Value = piece;

        await using var lecteur = await commande.ExecuteReaderAsync(cancellation);
        if (!await lecteur.ReadAsync(cancellation)) return null;

        return new SageDocumentHeader
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
    }

    public async Task<List<SageDocumentLine>> GetInvoiceLinesAsync(
        string piece,
        CancellationToken cancellation = default)
    {
        await using var connexion = await OuvrirAsync(cancellation);
        await using var commande = Commande(connexion, RequeteLignes);
        commande.Parameters.Add("@domaine", System.Data.SqlDbType.SmallInt).Value = DomaineVente;
        commande.Parameters.Add("@piece", System.Data.SqlDbType.VarChar, 50).Value = piece;

        var lignes = new List<SageDocumentLine>();
        await using var lecteur = await commande.ExecuteReaderAsync(cancellation);
        while (await lecteur.ReadAsync(cancellation))
        {
            lignes.Add(new SageDocumentLine
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
            });
        }

        logger.LogDebug("Pièce {Piece} : {Nombre} ligne(s) lue(s).", piece, lignes.Count);
        return lignes;
    }

    public async Task<SageCustomer?> GetCustomerAsync(string ctNum, CancellationToken cancellation = default)
    {
        await using var connexion = await OuvrirAsync(cancellation);
        await using var commande = Commande(connexion, RequeteClient);
        commande.Parameters.Add("@ctNum", System.Data.SqlDbType.VarChar, 50).Value = ctNum;

        await using var lecteur = await commande.ExecuteReaderAsync(cancellation);
        if (!await lecteur.ReadAsync(cancellation)) return null;

        return new SageCustomer
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
    }

    public async Task<List<SageTaxDefinition>> GetTaxesAsync(CancellationToken cancellation = default)
    {
        await using var connexion = await OuvrirAsync(cancellation);
        await using var commande = Commande(connexion, RequeteTaxes);

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

    private async Task<SqlConnection> OuvrirAsync(CancellationToken cancellation)
    {
        var connexion = new SqlConnection(connectionString);
        await connexion.OpenAsync(cancellation);
        return connexion;
    }

    private static SqlCommand Commande(SqlConnection connexion, string sql) =>
        new(ReadOnlyGuard.Verify(sql), connexion) { CommandType = System.Data.CommandType.Text };
}
