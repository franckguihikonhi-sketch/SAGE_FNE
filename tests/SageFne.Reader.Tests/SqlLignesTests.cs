using SageFne.Reader.Data;

namespace SageFne.Reader.Tests;

/// <summary>
/// La règle de rattachement des lignes à leur entête vit dans le texte de la
/// requête : c'est donc lui qu'il faut tenir.
/// </summary>
public class SqlLignesTests
{
    private static string Requete(InvoiceQuery query) =>
        SageInvoiceRepository.SqlLignes(new CritereSql("e"), query);

    /// <summary>L'indentation du SQL n'est pas la règle : le texte l'est.</summary>
    private static string Aplatir(string sql) =>
        string.Join(" ", sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void Une_piece_isolee_se_rattache_par_le_numero_et_le_type()
    {
        var sql = Requete(InvoiceQuery.Piece("1219"));

        // Sans le filtre de type, un bon de livraison 1219 apporterait ses
        // lignes à la facture 1219.
        Assert.Contains("e.DO_Piece = l.DO_Piece", sql);
        Assert.Contains("l.DO_Type in (@typeFacture, @typeComptabilisee)", sql);
        Assert.Contains("e.DO_Type in (@typeFacture, @typeComptabilisee)", sql);
        Assert.Contains("e.DO_Piece in (@piece0)", sql);
    }

    [Fact]
    public void Une_piece_isolee_et_un_lot_suivent_la_meme_regle()
    {
        var unitaire = Requete(InvoiceQuery.Piece("1219"));
        var lot = Requete(new InvoiceQuery { Depuis = new DateTime(2025, 12, 1) });

        // Seul le critère change ; la jointure, elle, est la même.
        const string jointure =
            "where e.DO_Domaine = l.DO_Domaine and e.DO_Piece = l.DO_Piece " +
            "and e.DO_Type in (@typeFacture, @typeComptabilisee)";

        Assert.Contains(jointure, Aplatir(unitaire));
        Assert.Contains(jointure, Aplatir(lot));
    }

    [Fact]
    public void Le_critere_de_periode_borne_les_deux_cotes()
    {
        var sql = Requete(new InvoiceQuery
        {
            Depuis = new DateTime(2025, 12, 1),
            Jusqua = new DateTime(2026, 1, 1),
        });

        Assert.Contains("e.DO_Date >= @depuis", sql);
        Assert.Contains("e.DO_Date < @jusqua", sql);
        Assert.DoesNotContain("@piece0", sql);
    }

    [Fact]
    public void La_comptabilisation_ne_fait_pas_disparaitre_les_lignes()
    {
        // L'entête passe de DO_Type 6 à 7 quand la facture est comptabilisée.
        // Exiger e.DO_Type = l.DO_Type ramènerait zéro ligne si F_DOCLIGNE
        // n'avait pas suivi au même instant : les deux côtés sont bornés à la
        // famille {6, 7}, pas contraints à l'égalité.
        var sql = Aplatir(Requete(InvoiceQuery.Piece("1219")));

        Assert.DoesNotContain("e.DO_Type = l.DO_Type", sql);
        Assert.Contains("l.DO_Type in (@typeFacture, @typeComptabilisee)", sql);
    }

    [Fact]
    public void La_requete_reste_une_lecture()
    {
        // Le garde-fou doit l'accepter : c'est lui qui la laissera partir.
        var sql = Requete(InvoiceQuery.Piece("1219"));

        Assert.Equal(sql.Trim(), ReadOnlyGuard.Verify(sql));
    }
}
