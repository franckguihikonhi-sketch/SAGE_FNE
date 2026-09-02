using SageFne.Core.Data;

namespace SageFne.Core.Tests;

/// <summary>
/// Une liste de colonnes écrite en dur est un pari sur une version de Sage.
/// Le pari a été perdu : le dossier HT n'a pas DL_DocType, et toute la lecture
/// des lignes échouait sur ce seul nom.
/// </summary>
public class ColonnesTableTests
{
    private static ColonnesTable Table(params string[] presentes) =>
        new("F_DOCLIGNE", new HashSet<string>(presentes, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Une_colonne_absente_ne_figure_pas_dans_le_select()
    {
        var colonnes = Table("DO_Piece", "DL_Ligne", "DL_Qte");

        var selection = colonnes.Selection("l", ["DO_Piece", "DL_DocType", "DL_Qte"]);

        Assert.Equal("l.DO_Piece, l.DL_Qte", selection);
        Assert.DoesNotContain("DL_DocType", selection);
    }

    [Fact]
    public void Les_colonnes_absentes_se_relevent()
    {
        var colonnes = Table("DO_Piece", "DL_Ligne");

        Assert.Equal(["DL_DocType"], colonnes.Absentes(["DO_Piece", "DL_DocType"]));
    }

    [Fact]
    public void La_casse_de_Sage_n_est_pas_une_difference()
    {
        var colonnes = Table("do_piece");

        Assert.True(colonnes.A("DO_Piece"));
    }

    [Fact]
    public void Une_colonne_indispensable_absente_leve_une_erreur_qui_la_nomme()
    {
        var colonnes = Table("DO_Piece");

        // Mieux vaut une erreur lisible qu'un « Invalid column name » de SQL
        // Server au milieu d'un lot.
        var erreur = Assert.Throws<InvalidOperationException>(
            () => colonnes.Exiger(["DO_Piece", "DL_Qte", "DL_PrixUnitaire"]));

        Assert.Contains("DL_Qte", erreur.Message);
        Assert.Contains("DL_PrixUnitaire", erreur.Message);
        Assert.Contains("F_DOCLIGNE", erreur.Message);
    }

    [Fact]
    public void Rien_ne_manque_ne_leve_rien()
    {
        Table("DO_Piece", "DL_Qte").Exiger(["DO_Piece", "DL_Qte"]);
    }

    [Fact]
    public void La_lecture_des_lignes_ne_demande_plus_DL_DocType()
    {
        // F_DOCLIGNE ne porte aucun équivalent de DO_DocType : le type d'origine
        // se lit sur l'entête, et nulle part ailleurs.
        Assert.DoesNotContain("DL_DocType", SageInvoiceRepository.SouhaiteesLignes);
        Assert.DoesNotContain(
            SageInvoiceRepository.SouhaiteesLignes,
            colonne => colonne.Contains("DocType", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Les_lignes_se_rattachent_par_des_colonnes_qui_existent()
    {
        foreach (var colonne in new[] { "DO_Domaine", "DO_Type", "DO_Piece" })
        {
            Assert.Contains(colonne, SageInvoiceRepository.SouhaiteesLignes);
        }
    }

    [Fact]
    public void Le_catalogue_se_consulte_en_lecture()
    {
        var sql = SageInvoiceRepository.SqlColonnesDeTable;

        Assert.Equal(sql.Trim(), ReadOnlyGuard.Verify(sql));
        Assert.Contains("sys.columns", sql);
        Assert.Contains("sys.tables", sql);
        Assert.Contains("@table", sql);
    }
}
