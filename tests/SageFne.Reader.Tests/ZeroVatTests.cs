using SageFne.Reader.Configuration;
using SageFne.Reader.Mapping;
using SageFne.Reader.Models.Sage;

namespace SageFne.Reader.Tests;

/// <summary>
/// TVAC et TVAD valent tous deux 0 % dans la nomenclature FNE, et Sage ne porte
/// pas la différence. Deviner reviendrait à déclarer à la DGI un régime fiscal
/// qu'on ignore, sur une facture qui ne se corrige plus qu'avec un avoir.
/// </summary>
public class ZeroVatTests
{
    private static SageDocumentLine Ligne(
        decimal taxe1 = 0m,
        string code1 = "",
        decimal taxe2 = 0m,
        string code2 = "",
        string article = "13415001",
        string ctNum = "4111SITASARL") => new()
    {
        Domaine = 0,
        Type = 6,
        Piece = "1219",
        Ligne = 1,
        CtNum = ctNum,
        ArticleReference = article,
        Designation = "Queue De Boeuf PV - Friboi",
        Quantite = 196.39m,
        PrixUnitaire = 2500m,
        Taxe1 = taxe1,
        CodeTaxe1 = code1,
        Taxe2 = taxe2,
        CodeTaxe2 = code2,
    };

    private static SageCustomer Client(string ctNum = "4111SITASARL") => new()
    {
        CtNum = ctNum,
        Intitule = "SITA SARL",
        Identifiant = "1432262S",
    };

    // --- Les mappings confirmés, qui ne bougent pas ------------------------

    [Fact]
    public void Dix_huit_pour_cent_reste_TVA()
    {
        Assert.Equal(["TVA"], TaxMapping.Read(Ligne(taxe1: 18m, code1: "TVA")).Taxes);
    }

    [Fact]
    public void Neuf_pour_cent_reste_TVAB()
    {
        Assert.Equal(["TVAB"], TaxMapping.Read(Ligne(taxe1: 9m, code1: "TVA")).Taxes);
    }

    [Fact]
    public void Un_taux_reconnu_ne_reclame_aucun_regime()
    {
        Assert.False(TaxMapping.Read(Ligne(taxe1: 18m, code1: "TVA")).RegimeZeroRequis);
        Assert.False(TaxMapping.Read(Ligne(taxe1: 9m, code1: "TVA")).RegimeZeroRequis);
    }

    // --- Le zéro, qui ne se devine plus ------------------------------------

    [Fact]
    public void Zero_pour_cent_sans_regime_ne_donne_aucun_code()
    {
        var resultat = TaxMapping.Read(Ligne(), RegimeTvaZero.Inconnu);

        Assert.Empty(resultat.Taxes);
        Assert.True(resultat.RegimeZeroRequis);
        Assert.Contains("TVAC", resultat.Avertissements[0]);
        Assert.Contains("TVAD", resultat.Avertissements[0]);
    }

    [Fact]
    public void Zero_pour_cent_n_est_plus_TVAD_par_defaut()
    {
        // La règle d'avant, désormais interdite.
        Assert.DoesNotContain("TVAD", TaxMapping.Read(Ligne()).Taxes);
    }

    [Theory]
    [InlineData(RegimeTvaZero.ExonerationConventionnelle, "TVAC")]
    [InlineData(RegimeTvaZero.ExonerationLegaleTeeRme, "TVAD")]
    public void Un_regime_declare_donne_son_code(RegimeTvaZero regime, string attendu)
    {
        var resultat = TaxMapping.Read(Ligne(), regime);

        Assert.Equal([attendu], resultat.Taxes);
        Assert.False(resultat.RegimeZeroRequis);
    }

    [Fact]
    public void L_AIRSI_part_meme_quand_le_regime_est_inconnu()
    {
        // Le prélèvement ne dépend pas du régime de TVA : la pièce est bloquée,
        // mais le customTaxes reste juste.
        var resultat = TaxMapping.Read(Ligne(taxe2: 1.5m, code2: "AIRSI"), RegimeTvaZero.Inconnu);

        Assert.True(resultat.RegimeZeroRequis);
        var prelevement = Assert.Single(resultat.CustomTaxes);
        Assert.Equal("AIRSI", prelevement.Name);
        Assert.Equal(1.5m, prelevement.Amount);
    }

    [Fact]
    public void Un_taux_hors_nomenclature_n_est_pas_une_exoneration()
    {
        // 12 % n'est pas 0 % : la ligne ne réclame pas un régime d'exonération,
        // elle porte un taux que la nomenclature ignore.
        var resultat = TaxMapping.Read(Ligne(taxe1: 12m, code1: "TVA"), RegimeTvaZero.ExonerationLegaleTeeRme);

        Assert.Empty(resultat.Taxes);
        Assert.False(resultat.RegimeZeroRequis);
    }

    // --- La classification, de la plus précise à la plus générale ----------

    private static ZeroVatClassifier Classeur(
        string global = "Unknown",
        Dictionary<string, string>? parArticle = null,
        Dictionary<string, string>? parClient = null) =>
        new(new FneOptions
        {
            ZeroVatCategory = global,
            ZeroVatCategoryByArticle = parArticle ?? new(StringComparer.OrdinalIgnoreCase),
            ZeroVatCategoryByCustomer = parClient ?? new(StringComparer.OrdinalIgnoreCase),
        });

    [Fact]
    public void Sans_rien_de_configure_le_regime_est_inconnu()
    {
        Assert.Equal(RegimeTvaZero.Inconnu, Classeur().Classer(Ligne(), Client()));
    }

    [Fact]
    public void Le_reglage_global_s_applique_a_defaut()
    {
        Assert.Equal(
            RegimeTvaZero.ExonerationLegaleTeeRme,
            Classeur(global: "LegalExemptionTEE_RME").Classer(Ligne(), Client()));
    }

    [Fact]
    public void Le_client_l_emporte_sur_le_reglage_global()
    {
        var classeur = Classeur(
            global: "LegalExemptionTEE_RME",
            parClient: new(StringComparer.OrdinalIgnoreCase) { ["4111SITASARL"] = "ConventionalExemption" });

        Assert.Equal(RegimeTvaZero.ExonerationConventionnelle, classeur.Classer(Ligne(), Client()));
    }

    [Fact]
    public void L_article_l_emporte_sur_le_client()
    {
        // L'exonération d'un produit prime celle du titulaire : c'est la règle
        // la plus précise qui gagne.
        var classeur = Classeur(
            parArticle: new(StringComparer.OrdinalIgnoreCase) { ["13415001"] = "LegalExemptionTEE_RME" },
            parClient: new(StringComparer.OrdinalIgnoreCase) { ["4111SITASARL"] = "ConventionalExemption" });

        Assert.Equal(RegimeTvaZero.ExonerationLegaleTeeRme, classeur.Classer(Ligne(), Client()));
    }

    [Fact]
    public void Une_valeur_illisible_ne_vaut_pas_classification()
    {
        // « TVAX » n'existe pas : mieux vaut bloquer qu'appliquer un régime
        // mal orthographié.
        var classeur = Classeur(
            parClient: new(StringComparer.OrdinalIgnoreCase) { ["4111SITASARL"] = "TVAX" });

        Assert.Equal(RegimeTvaZero.Inconnu, classeur.Classer(Ligne(), Client()));
    }

    [Theory]
    [InlineData("TVAC", RegimeTvaZero.ExonerationConventionnelle)]
    [InlineData("ConventionalExemption", RegimeTvaZero.ExonerationConventionnelle)]
    [InlineData("TVAD", RegimeTvaZero.ExonerationLegaleTeeRme)]
    [InlineData("LegalExemptionTEE_RME", RegimeTvaZero.ExonerationLegaleTeeRme)]
    [InlineData("Unknown", RegimeTvaZero.Inconnu)]
    [InlineData("", RegimeTvaZero.Inconnu)]
    public void Le_parametrage_se_lit_sous_ses_deux_ecritures(string valeur, RegimeTvaZero attendu)
    {
        Assert.Equal(attendu, ZeroVatClassifier.Analyser(valeur));
    }

    [Fact]
    public void Une_valeur_inconnue_du_parametrage_se_distingue_d_Unknown()
    {
        // null, et non Inconnu : la valeur est fautive, pas absente.
        Assert.Null(ZeroVatClassifier.Analyser("n'importe quoi"));
    }

    [Fact]
    public void Les_codes_FNE_correspondent_a_la_nomenclature()
    {
        Assert.Equal("TVAC", RegimeTvaZero.ExonerationConventionnelle.Code());
        Assert.Equal("TVAD", RegimeTvaZero.ExonerationLegaleTeeRme.Code());
        Assert.Null(RegimeTvaZero.Inconnu.Code());
    }
}
