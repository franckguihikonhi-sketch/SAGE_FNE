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

    // --- La hiérarchie, du plus précis au plus général ---------------------

    private static ConfiguredZeroVatPolicy Politique(
        string dossier = "Unknown",
        Dictionary<string, string>? parArticle = null,
        Dictionary<string, string>? parFamille = null,
        Dictionary<string, string>? parClient = null) =>
        new(new ZeroVatOptions
        {
            Default = dossier,
            ByArticle = parArticle ?? new(StringComparer.OrdinalIgnoreCase),
            ByFamily = parFamille ?? new(StringComparer.OrdinalIgnoreCase),
            ByCustomer = parClient ?? new(StringComparer.OrdinalIgnoreCase),
        });

    private static Dictionary<string, string> Regle(string cle, string valeur) =>
        new(StringComparer.OrdinalIgnoreCase) { [cle] = valeur };

    private static readonly ZeroVatContexte Contexte =
        new("13415001", "02", "4111SITASARL");

    [Fact]
    public void Sans_aucune_regle_le_regime_est_inconnu()
    {
        var decision = Politique().Decider(Contexte);

        Assert.Equal(RegimeTvaZero.Inconnu, decision.Regime);
        Assert.Equal("aucune règle applicable", decision.Origine);
        Assert.Null(decision.Erreur);
    }

    [Fact]
    public void Priorite_4_le_dossier_s_applique_a_defaut()
    {
        var decision = Politique(dossier: "LegalExemptionTEE_RME").Decider(Contexte);

        Assert.Equal(RegimeTvaZero.ExonerationLegaleTeeRme, decision.Regime);
        Assert.Equal("dossier", decision.Origine);
    }

    [Fact]
    public void Priorite_3_le_client_l_emporte_sur_le_dossier()
    {
        var decision = Politique(
            dossier: "LegalExemptionTEE_RME",
            parClient: Regle("4111SITASARL", "ConventionalExemption")).Decider(Contexte);

        Assert.Equal(RegimeTvaZero.ExonerationConventionnelle, decision.Regime);
        Assert.Equal("client 4111SITASARL", decision.Origine);
    }

    [Fact]
    public void Priorite_2_la_famille_l_emporte_sur_le_client()
    {
        var decision = Politique(
            dossier: "ConventionalExemption",
            parFamille: Regle("02", "LegalExemptionTEE_RME"),
            parClient: Regle("4111SITASARL", "ConventionalExemption")).Decider(Contexte);

        Assert.Equal(RegimeTvaZero.ExonerationLegaleTeeRme, decision.Regime);
        Assert.Equal("famille 02", decision.Origine);
    }

    [Fact]
    public void Priorite_1_l_article_l_emporte_sur_tout()
    {
        var decision = Politique(
            dossier: "ConventionalExemption",
            parArticle: Regle("13415001", "LegalExemptionTEE_RME"),
            parFamille: Regle("02", "ConventionalExemption"),
            parClient: Regle("4111SITASARL", "ConventionalExemption")).Decider(Contexte);

        Assert.Equal(RegimeTvaZero.ExonerationLegaleTeeRme, decision.Regime);
        Assert.Equal("article 13415001", decision.Origine);
    }

    [Fact]
    public void Les_quatre_niveaux_s_enchainent_dans_l_ordre()
    {
        var toutes = Politique(
            dossier: "ConventionalExemption",
            parArticle: Regle("13415001", "LegalExemptionTEE_RME"),
            parFamille: Regle("02", "ConventionalExemption"),
            parClient: Regle("4111SITASARL", "ConventionalExemption"));

        Assert.Equal("article 13415001", toutes.Decider(Contexte).Origine);
        // Un autre article : la famille prend le relais.
        Assert.Equal("famille 02", toutes.Decider(Contexte with { ArticleReference = "AUTRE" }).Origine);
        // Ni article ni famille connus : le client.
        Assert.Equal("client 4111SITASARL",
            toutes.Decider(new ZeroVatContexte("AUTRE", "99", "4111SITASARL")).Origine);
        // Rien de connu : le dossier.
        Assert.Equal("dossier", toutes.Decider(new ZeroVatContexte("AUTRE", "99", "AUTRE")).Origine);
    }

    [Fact]
    public void Une_famille_absente_ne_fait_pas_echouer_la_cascade()
    {
        // F_ARTICLE peut ne pas porter FA_CodeFamille : la famille est alors
        // vide, et le niveau est simplement sauté.
        var decision = Politique(
            parFamille: Regle("02", "ConventionalExemption"),
            parClient: Regle("4111SITASARL", "LegalExemptionTEE_RME"))
            .Decider(Contexte with { Famille = "" });

        Assert.Equal("client 4111SITASARL", decision.Origine);
    }

    // --- Aucune valeur n'est acceptée en silence ---------------------------

    [Theory]
    [InlineData("TVAC")]
    [InlineData("TVAD")]
    [InlineData("conventionnelle")]
    [InlineData("legale")]
    [InlineData("n'importe quoi")]
    public void Une_valeur_hors_nomenclature_est_refusee_et_non_ignoree(string valeur)
    {
        // Passer au niveau suivant traiterait une faute de frappe comme une
        // absence de règle : la facture partirait sous un régime non voulu.
        var decision = Politique(
            dossier: "LegalExemptionTEE_RME",
            parArticle: Regle("13415001", valeur)).Decider(Contexte);

        Assert.Equal(RegimeTvaZero.Inconnu, decision.Regime);
        Assert.NotNull(decision.Erreur);
        Assert.Contains(valeur, decision.Erreur);
        Assert.Contains("ConventionalExemption", decision.Erreur);
        Assert.Contains("LegalExemptionTEE_RME", decision.Erreur);
    }

    [Fact]
    public void Un_reglage_de_dossier_illisible_est_refuse_aussi()
    {
        var decision = Politique(dossier: "TVAD").Decider(Contexte);

        Assert.Equal(RegimeTvaZero.Inconnu, decision.Regime);
        Assert.NotNull(decision.Erreur);
    }

    [Theory]
    [InlineData("ConventionalExemption", RegimeTvaZero.ExonerationConventionnelle)]
    [InlineData("LegalExemptionTEE_RME", RegimeTvaZero.ExonerationLegaleTeeRme)]
    [InlineData("Unknown", RegimeTvaZero.Inconnu)]
    [InlineData("", RegimeTvaZero.Inconnu)]
    public void Seules_les_valeurs_de_la_nomenclature_sont_lues(string valeur, RegimeTvaZero attendu)
    {
        Assert.Equal(attendu, ConfiguredZeroVatPolicy.Analyser(valeur));
    }

    [Fact]
    public void Une_valeur_inconnue_se_distingue_d_Unknown()
    {
        // null, et non Inconnu : la valeur est fautive, pas absente.
        Assert.Null(ConfiguredZeroVatPolicy.Analyser("TVAD"));
        Assert.Equal(RegimeTvaZero.Inconnu, ConfiguredZeroVatPolicy.Analyser("Unknown"));
    }

    [Fact]
    public void Un_Unknown_declare_bloque_comme_une_absence_de_regle()
    {
        var decision = Politique(
            dossier: "LegalExemptionTEE_RME",
            parArticle: Regle("13415001", "Unknown")).Decider(Contexte);

        Assert.Equal(RegimeTvaZero.Inconnu, decision.Regime);
        Assert.Equal("article 13415001", decision.Origine);
        Assert.Null(decision.Erreur);
    }

    [Fact]
    public void Les_codes_FNE_correspondent_a_la_nomenclature()
    {
        Assert.Equal("TVAC", RegimeTvaZero.ExonerationConventionnelle.Code());
        Assert.Equal("TVAD", RegimeTvaZero.ExonerationLegaleTeeRme.Code());
        Assert.Null(RegimeTvaZero.Inconnu.Code());
    }
}

/// <summary>
/// Le régime fiscal de l'acheteur, et ce qu'il ne peut pas faire.
/// </summary>
/// <remarks>
/// TEE et RME tiennent au statut de l'acheteur, non à la nature du produit :
/// quand les deux s'appliquent, c'est le statut qui fonde l'exonération devant
/// la DGI. D'où sa priorité sur les règles d'article et de famille.
///
/// Il ne détaxe rien pour autant : une ligne à 9 % ou 18 % ne consulte jamais
/// ces règles, et un client TEE achetant un produit taxé paie sa TVA.
/// </remarks>
public class RegimeAcheteurTests
{
    private static SageDocumentLine Ligne(decimal taux, string article = "25SN001") => new()
    {
        Piece = "1", Domaine = 0, Type = 6, Ligne = 1000,
        ArticleReference = article, Designation = "Sardine",
        Quantite = 1m, PrixUnitaire = 1000m, MontantHT = 1000m,
        CodeTaxe1 = taux == 0m ? "" : "TVA", Taxe1 = taux,
    };

    private static ConfiguredZeroVatPolicy Politique(
        Dictionary<string, string>? regimes = null,
        Dictionary<string, string>? parArticle = null,
        Dictionary<string, string>? parFamille = null,
        string defaut = "Unknown") =>
        new(new ZeroVatOptions
        {
            CustomerTaxRegimes = new(regimes ?? [], StringComparer.OrdinalIgnoreCase),
            ByArticle = new(parArticle ?? [], StringComparer.OrdinalIgnoreCase),
            ByFamily = new(parFamille ?? [], StringComparer.OrdinalIgnoreCase),
            Default = defaut,
        });

    /// <summary>Le code FNE réellement envoyé, régime compris.</summary>
    private static IReadOnlyList<string> Codes(
        ConfiguredZeroVatPolicy politique, SageDocumentLine ligne,
        string client = "4111SOGEL", string famille = "01")
    {
        var decision = politique.Decider(new ZeroVatContexte(ligne.ArticleReference, famille, client));
        return TaxMapping.Read(ligne, decision.Regime).Taxes;
    }

    [Theory]
    [InlineData("RME")]
    [InlineData("TEE")]
    [InlineData("rme")]
    [InlineData("  tee  ")]
    public void Un_client_au_regime_declare_donne_TVAD_sur_une_ligne_a_zero(string regime)
    {
        var politique = Politique(regimes: new() { ["4111SOGEL"] = regime });

        Assert.Equal(["TVAD"], Codes(politique, Ligne(0m)));
    }

    [Theory]
    [InlineData(9, "TVAB")]
    [InlineData(18, "TVA")]
    public void Un_client_au_regime_declare_paie_sa_TVA_sur_une_ligne_taxee(decimal taux, string attendu)
    {
        // Le garde-fou qui compte : le régime ne détaxe pas. Il explique une
        // exonération constatée, il ne la crée pas.
        var politique = Politique(regimes: new() { ["4111SOGEL"] = "RME" });

        var codes = Codes(politique, Ligne(taux));

        Assert.Equal([attendu], codes);
        Assert.DoesNotContain("TVAD", codes);
    }

    [Fact]
    public void Le_regime_de_l_acheteur_prime_sur_la_regle_d_article()
    {
        // Les deux s'appliquent : c'est le statut de l'acheteur qui fonde
        // l'exonération devant la DGI, pas la nature du produit.
        var politique = Politique(
            regimes: new() { ["4111SOGEL"] = "RME" },
            parArticle: new() { ["25SN001"] = ConfiguredZeroVatPolicy.Conventionnelle });

        var decision = politique.Decider(new ZeroVatContexte("25SN001", "01", "4111SOGEL"));

        Assert.Equal(RegimeTvaZero.ExonerationLegaleTeeRme, decision.Regime);
        Assert.Contains("régime acheteur RME", decision.Origine);
    }

    [Fact]
    public void Le_regime_de_l_acheteur_prime_sur_la_regle_de_famille()
    {
        var politique = Politique(
            regimes: new() { ["4111SOGEL"] = "TEE" },
            parFamille: new() { ["01"] = ConfiguredZeroVatPolicy.Conventionnelle });

        var decision = politique.Decider(new ZeroVatContexte("25SN001", "01", "4111SOGEL"));

        Assert.Equal(RegimeTvaZero.ExonerationLegaleTeeRme, decision.Regime);
    }

    [Fact]
    public void Sans_regime_declare_les_regles_produit_reprennent_la_main()
    {
        var politique = Politique(
            regimes: new() { ["4111AUTRE"] = "RME" },
            parArticle: new() { ["25SN001"] = ConfiguredZeroVatPolicy.Conventionnelle });

        var decision = politique.Decider(new ZeroVatContexte("25SN001", "01", "4111SOGEL"));

        Assert.Equal(RegimeTvaZero.ExonerationConventionnelle, decision.Regime);
        Assert.Contains("article", decision.Origine);
    }

    [Fact]
    public void Un_client_sans_regime_ni_autre_regle_reste_bloque()
    {
        var politique = Politique();

        var decision = politique.Decider(new ZeroVatContexte("25SN001", "01", "4111SOGEL"));

        Assert.Equal(RegimeTvaZero.Inconnu, decision.Regime);
        Assert.Empty(Codes(politique, Ligne(0m)));
        Assert.True(TaxMapping.Read(Ligne(0m), decision.Regime).RegimeZeroRequis);
    }

    [Theory]
    [InlineData("TEE/RME")]
    [InlineData("LegalExemptionTEE_RME")]
    [InlineData("legal")]
    [InlineData("")]
    [InlineData("   ")]
    public void Un_regime_hors_nomenclature_est_refuse_et_signale(string valeur)
    {
        // Passer au niveau suivant traiterait une faute de frappe comme une
        // absence de règle : la facture partirait sous un régime non voulu.
        var politique = Politique(
            regimes: new() { ["4111SOGEL"] = valeur },
            parArticle: new() { ["25SN001"] = ConfiguredZeroVatPolicy.Conventionnelle });

        var decision = politique.Decider(new ZeroVatContexte("25SN001", "01", "4111SOGEL"));

        Assert.Equal(RegimeTvaZero.Inconnu, decision.Regime);
        Assert.NotNull(decision.Erreur);
        Assert.Contains("TEE", decision.Erreur);
        Assert.Contains("RME", decision.Erreur);
    }

    [Fact]
    public void L_origine_de_la_decision_nomme_le_regime_et_le_client()
    {
        // Ce que « apercu » affichera : un exploitant doit pouvoir vérifier
        // d'où vient le code envoyé.
        var decision = Politique(regimes: new() { ["4111SOGEL"] = "rme" })
            .Decider(new ZeroVatContexte("25SN001", "01", "4111SOGEL"));

        Assert.Contains("RME", decision.Origine);
        Assert.Contains("4111SOGEL", decision.Origine);
    }

    [Fact]
    public void Le_regime_ne_se_deduit_d_aucun_historique()
    {
        // La garantie structurelle : la décision ne voit que trois clés — un
        // article, une famille, un compte. Aucune facture, aucun cumul, aucun
        // historique. Un client dont tout est à 0 % reste un client dont on
        // ignore le régime.
        var contexte = typeof(ZeroVatContexte).GetProperties().Select(p => p.Name).ToList();

        Assert.Equal(3, contexte.Count);
        Assert.DoesNotContain(contexte, nom =>
            nom.Contains("Historique", StringComparison.OrdinalIgnoreCase)
            || nom.Contains("Facture", StringComparison.OrdinalIgnoreCase)
            || nom.Contains("Lignes", StringComparison.OrdinalIgnoreCase));

        // Et un client sans déclaration reste bloqué, quel que soit son passé.
        Assert.Equal(
            RegimeTvaZero.Inconnu,
            Politique().Decider(new ZeroVatContexte("25SN001", "01", "4111SOGEL")).Regime);
    }
}
