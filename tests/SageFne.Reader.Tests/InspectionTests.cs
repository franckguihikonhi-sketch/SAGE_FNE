using SageFne.Core.Data;
using SageFne.Core.Models.Sage;

namespace SageFne.Core.Tests;

/// <summary>
/// Les commandes d'exploration désignent des tables depuis l'extérieur. Un nom
/// de table ne peut pas être passé en paramètre SQL : il s'écrit dans la
/// requête. C'est le seul endroit du projet où du texte entre dans le SQL, et
/// il doit être tenu.
/// </summary>
public class IdentifiantSqlTests
{
    [Theory]
    [InlineData("F_TAXE")]
    [InlineData("F_DOCLIGNE")]
    [InlineData("CT_Num")]
    [InlineData("DL_Remise01REM_Type")]
    [InlineData("_interne")]
    public void Un_nom_de_table_Sage_passe(string nom)
    {
        Assert.Equal(nom, IdentifiantSql.Verifier(nom));
    }

    [Theory]
    [InlineData("F_TAXE; drop table F_TAXE")]
    [InlineData("F_TAXE--")]
    [InlineData("F_TAXE where 1=1")]
    [InlineData("F TAXE")]
    [InlineData("F_TAXE'")]
    [InlineData("")]
    [InlineData("1TABLE")]
    [InlineData("F_TAXE)")]
    public void Tout_ce_qui_n_est_pas_un_identifiant_est_refuse(string nom)
    {
        Assert.Throws<ArgumentException>(() => IdentifiantSql.Verifier(nom));
    }

    [Fact]
    public void Les_espaces_autour_sont_tolerés()
    {
        Assert.Equal("F_TAXE", IdentifiantSql.Verifier("  F_TAXE  "));
    }
}

/// <summary>
/// Chercher une information dont on ignore le nom de colonne suppose de tout
/// voir, puis de savoir porter le regard.
/// </summary>
public class SageEnregistrementTests
{
    private static SageEnregistrement Fiche(params (string Colonne, string Valeur)[] champs) => new()
    {
        Table = "F_COMPTET",
        Cle = "4111SITASARL",
        Champs = champs.Select(champ => new SageChamp(champ.Colonne, champ.Valeur)).ToList(),
    };

    [Fact]
    public void Une_colonne_vide_ou_a_zero_ne_porte_rien()
    {
        var fiche = Fiche(("CT_Num", "4111SITASARL"), ("CT_Vide", ""), ("CT_Zero", "0"));

        Assert.Equal(["CT_Num"], fiche.Renseignes.Select(champ => champ.Colonne));
    }

    [Fact]
    public void Les_colonnes_a_regarder_se_reperent_par_leur_nom()
    {
        var fiche = Fiche(
            ("CT_Num", "4111SITASARL"),
            ("CT_Identifiant", "14322625"),
            ("CT_TypeNIF", "1"),
            ("CT_Telephone", "0700000000"),
            ("CT_CategorieComptable", "EXO"));

        var noms = fiche.Fiscaux.Select(champ => champ.Colonne).ToList();

        Assert.Contains("CT_Identifiant", noms);
        Assert.Contains("CT_TypeNIF", noms);
        Assert.Contains("CT_CategorieComptable", noms);
        Assert.DoesNotContain("CT_Telephone", noms);
    }

    [Fact]
    public void Une_valeur_se_relit_par_son_nom_de_colonne()
    {
        var fiche = Fiche(("AR_Ref", "13415001"));

        Assert.Equal("13415001", fiche.Valeur("ar_ref"));
        Assert.Null(fiche.Valeur("AR_Inexistant"));
    }

    [Fact]
    public async Task Le_jeu_d_essai_rend_les_fiches_de_taxe()
    {
        var depot = new DemoSageInvoiceRepository();

        var taxes = await depot.LireTableAsync("F_TAXE");

        Assert.Equal(3, taxes.Count);
        Assert.Contains(taxes, taxe => taxe.Cle == "AIRSI");
        Assert.All(taxes, taxe => Assert.Contains(taxe.Champs, champ => champ.Colonne == "TA_Taux"));
    }

    [Fact]
    public async Task La_fiscalite_brute_d_une_ligne_montre_les_trois_emplacements()
    {
        var depot = new DemoSageInvoiceRepository();

        var lignes = await depot.LireFiscaliteLignesAsync("1219");

        var ligne = Assert.Single(lignes);
        foreach (var colonne in new[] { "DL_Taxe1", "DL_CodeTaxe1", "DL_Taxe2", "DL_CodeTaxe2", "DL_Taxe3" })
        {
            Assert.Contains(ligne.Champs, champ => champ.Colonne == colonne);
        }

        // La 1219 est exonérée et soumise à l'AIRSI : c'est le brut, sans
        // interprétation.
        Assert.Equal("0", ligne.Valeur("DL_Taxe1"));
        Assert.Equal("AIRSI", ligne.Valeur("DL_CodeTaxe2"));
    }

    [Fact]
    public async Task Une_table_inconnue_du_jeu_d_essai_ne_plante_pas()
    {
        var depot = new DemoSageInvoiceRepository();

        Assert.Empty(await depot.LireTableAsync("F_INEXISTANTE"));
        Assert.Null(await depot.LireLigneAsync("F_INEXISTANTE", "X", "Y"));
    }
}
