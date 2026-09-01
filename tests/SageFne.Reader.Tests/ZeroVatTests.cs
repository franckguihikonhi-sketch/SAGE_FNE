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
        var resultat = TaxMapping.Read(Ligne(), CodeTvaZero.Inconnu);

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
    [InlineData(CodeTvaZero.Tvac, "TVAC")]
    [InlineData(CodeTvaZero.Tvad, "TVAD")]
    public void Un_regime_declare_donne_son_code(CodeTvaZero regime, string attendu)
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
        var resultat = TaxMapping.Read(Ligne(taxe2: 1.5m, code2: "AIRSI"), CodeTvaZero.Inconnu);

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
        var resultat = TaxMapping.Read(Ligne(taxe1: 12m, code1: "TVA"), CodeTvaZero.Tvad);

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

        Assert.Equal(CodeTvaZero.Inconnu, decision.Code);
        Assert.Equal("aucune règle applicable", decision.Origine);
        Assert.Null(decision.Erreur);
    }

    [Fact]
    public void Priorite_4_le_dossier_s_applique_a_defaut()
    {
        var decision = Politique(dossier: "LegalExemptionTEE_RME").Decider(Contexte);

        Assert.Equal(CodeTvaZero.Tvad, decision.Code);
        Assert.Equal("dossier", decision.Origine);
    }

    [Fact]
    public void Priorite_3_le_client_l_emporte_sur_le_dossier()
    {
        var decision = Politique(
            dossier: "LegalExemptionTEE_RME",
            parClient: Regle("4111SITASARL", "ConventionalExemption")).Decider(Contexte);

        Assert.Equal(CodeTvaZero.Tvac, decision.Code);
        Assert.Equal("client 4111SITASARL", decision.Origine);
    }

    [Fact]
    public void Priorite_2_la_famille_l_emporte_sur_le_client()
    {
        var decision = Politique(
            dossier: "ConventionalExemption",
            parFamille: Regle("02", "LegalExemptionTEE_RME"),
            parClient: Regle("4111SITASARL", "ConventionalExemption")).Decider(Contexte);

        Assert.Equal(CodeTvaZero.Tvad, decision.Code);
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

        Assert.Equal(CodeTvaZero.Tvad, decision.Code);
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
    [InlineData("conventionnelle")]
    [InlineData("legale")]
    [InlineData("TVAE")]
    [InlineData("n'importe quoi")]
    public void Une_valeur_hors_nomenclature_est_refusee_et_non_ignoree(string valeur)
    {
        // Passer au niveau suivant traiterait une faute de frappe comme une
        // absence de règle : la facture partirait sous un code non voulu.
        var decision = Politique(
            dossier: "Tvad",
            parArticle: Regle("13415001", valeur)).Decider(Contexte);

        Assert.Equal(CodeTvaZero.Inconnu, decision.Code);
        Assert.NotNull(decision.Erreur);
        Assert.Contains(valeur, decision.Erreur);
        Assert.Contains("Tvac", decision.Erreur);
        Assert.Contains("Tvad", decision.Erreur);
    }

    [Theory]
    [InlineData("Tvad", CodeTvaZero.Tvad)]
    [InlineData("TVAD", CodeTvaZero.Tvad)]
    [InlineData("tvad", CodeTvaZero.Tvad)]
    [InlineData("  Tvac  ", CodeTvaZero.Tvac)]
    [InlineData("TVAC", CodeTvaZero.Tvac)]
    public void Le_code_FNE_se_lit_quelle_qu_en_soit_la_graphie(string valeur, CodeTvaZero attendu)
    {
        // « TVAD » est la graphie de la documentation DGI : la refuser au motif
        // qu'on attend « Tvad » serait perverse.
        var decision = Politique(parArticle: Regle("13415001", valeur)).Decider(Contexte);

        Assert.Equal(attendu, decision.Code);
        Assert.Null(decision.Erreur);
        Assert.Null(decision.Avertissement);
    }

    [Fact]
    public void Un_reglage_de_dossier_illisible_est_refuse_aussi()
    {
        var decision = Politique(dossier: "TVAX").Decider(Contexte);

        Assert.Equal(CodeTvaZero.Inconnu, decision.Code);
        Assert.NotNull(decision.Erreur);
    }

    [Theory]
    [InlineData("ConventionalExemption", CodeTvaZero.Tvac)]
    [InlineData("LegalExemptionTEE_RME", CodeTvaZero.Tvad)]
    [InlineData("Unknown", CodeTvaZero.Inconnu)]
    [InlineData("", CodeTvaZero.Inconnu)]
    public void Seules_les_valeurs_de_la_nomenclature_sont_lues(string valeur, CodeTvaZero attendu)
    {
        Assert.Equal(attendu, ConfiguredZeroVatPolicy.Analyser(valeur));
    }

    [Fact]
    public void Une_valeur_inconnue_se_distingue_d_Unknown()
    {
        // null, et non Inconnu : la valeur est fautive, pas absente. « TVAD »
        // est devenu un code valide ; c'est une graphie fantaisiste qu'il faut
        // maintenant prendre pour éprouver la distinction.
        Assert.Null(ConfiguredZeroVatPolicy.Analyser("TVAZ"));
        Assert.Equal(CodeTvaZero.Inconnu, ConfiguredZeroVatPolicy.Analyser("Unknown"));
        Assert.Equal(CodeTvaZero.Tvad, ConfiguredZeroVatPolicy.Analyser("TVAD"));
    }

    [Fact]
    public void Un_Unknown_declare_bloque_comme_une_absence_de_regle()
    {
        var decision = Politique(
            dossier: "LegalExemptionTEE_RME",
            parArticle: Regle("13415001", "Unknown")).Decider(Contexte);

        Assert.Equal(CodeTvaZero.Inconnu, decision.Code);
        Assert.Equal("article 13415001", decision.Origine);
        Assert.Null(decision.Erreur);
    }

    [Fact]
    public void Les_codes_FNE_correspondent_a_la_nomenclature()
    {
        Assert.Equal("TVAC", CodeTvaZero.Tvac.Code());
        Assert.Equal("TVAD", CodeTvaZero.Tvad.Code());
        Assert.Null(CodeTvaZero.Inconnu.Code());
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
        return TaxMapping.Read(ligne, decision.Code).Taxes;
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
            parArticle: new() { ["25SN001"] = ConfiguredZeroVatPolicy.Tvac });

        var decision = politique.Decider(new ZeroVatContexte("25SN001", "01", "4111SOGEL"));

        Assert.Equal(CodeTvaZero.Tvad, decision.Code);
        Assert.Contains("régime acheteur RME", decision.Origine);
    }

    [Fact]
    public void Le_regime_de_l_acheteur_prime_sur_la_regle_de_famille()
    {
        var politique = Politique(
            regimes: new() { ["4111SOGEL"] = "TEE" },
            parFamille: new() { ["01"] = ConfiguredZeroVatPolicy.Tvac });

        var decision = politique.Decider(new ZeroVatContexte("25SN001", "01", "4111SOGEL"));

        Assert.Equal(CodeTvaZero.Tvad, decision.Code);
    }

    [Fact]
    public void Sans_regime_declare_les_regles_produit_reprennent_la_main()
    {
        var politique = Politique(
            regimes: new() { ["4111AUTRE"] = "RME" },
            parArticle: new() { ["25SN001"] = ConfiguredZeroVatPolicy.Tvac });

        var decision = politique.Decider(new ZeroVatContexte("25SN001", "01", "4111SOGEL"));

        Assert.Equal(CodeTvaZero.Tvac, decision.Code);
        Assert.Contains("article", decision.Origine);
    }

    [Fact]
    public void Un_client_sans_regime_ni_autre_regle_reste_bloque()
    {
        var politique = Politique();

        var decision = politique.Decider(new ZeroVatContexte("25SN001", "01", "4111SOGEL"));

        Assert.Equal(CodeTvaZero.Inconnu, decision.Code);
        Assert.Empty(Codes(politique, Ligne(0m)));
        Assert.True(TaxMapping.Read(Ligne(0m), decision.Code).RegimeZeroRequis);
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
            parArticle: new() { ["25SN001"] = ConfiguredZeroVatPolicy.Tvac });

        var decision = politique.Decider(new ZeroVatContexte("25SN001", "01", "4111SOGEL"));

        Assert.Equal(CodeTvaZero.Inconnu, decision.Code);
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
            CodeTvaZero.Inconnu,
            Politique().Decider(new ZeroVatContexte("25SN001", "01", "4111SOGEL")).Code);
    }
}

/// <summary>
/// Le code FNE et le fondement juridique sont deux choses.
/// </summary>
/// <remarks>
/// <c>LegalExemptionTEE_RME</c> les confondait : il nommait un fondement dans
/// un emplacement qui ne porte qu'un code. Posé sur un article de poisson
/// congelé — ce que la DGI pourrait demander — il aurait inscrit dans la
/// configuration, donc dans la piste d'audit, un régime TEE/RME que ni le
/// produit ni le client ne justifient.
/// </remarks>
public class CodeEtFondementTests
{
    private static ConfiguredZeroVatPolicy Politique(
        Dictionary<string, string>? parArticle = null,
        Dictionary<string, string>? regimes = null,
        string defaut = "Unknown") =>
        new(new ZeroVatOptions
        {
            CustomerTaxRegimes = new(regimes ?? [], StringComparer.OrdinalIgnoreCase),
            ByArticle = new(parArticle ?? [], StringComparer.OrdinalIgnoreCase),
            Default = defaut,
        });

    private static readonly ZeroVatContexte Contexte = new("25SN001", "01", "4111SOGEL");

    [Fact]
    public void Une_regle_d_article_ne_pretend_a_aucun_fondement()
    {
        // Elle dit quel code envoyer. Pourquoi, elle l'ignore — et le dire
        // serait mentir tant que la DGI n'a pas répondu.
        var decision = Politique(parArticle: new() { ["25SN001"] = "Tvad" }).Decider(Contexte);

        Assert.Equal(CodeTvaZero.Tvad, decision.Code);
        Assert.Equal(FondementExoneration.NonEtabli, decision.Fondement);
    }

    [Fact]
    public void Le_regime_de_l_acheteur_est_le_seul_fondement_etabli_aujourd_hui()
    {
        var decision = Politique(regimes: new() { ["4111SOGEL"] = "RME" }).Decider(Contexte);

        Assert.Equal(CodeTvaZero.Tvad, decision.Code);
        Assert.Equal(FondementExoneration.RegimeAcheteur, decision.Fondement);
    }

    [Fact]
    public void Le_code_ne_se_deduit_pas_du_fondement_ni_l_inverse()
    {
        // Deux règles donnant le même code, deux fondements différents. Les
        // confondre rendrait l'un des deux faux.
        var parRegime = Politique(regimes: new() { ["4111SOGEL"] = "TEE" }).Decider(Contexte);
        var parArticle = Politique(parArticle: new() { ["25SN001"] = "Tvad" }).Decider(Contexte);

        Assert.Equal(parRegime.Code, parArticle.Code);
        Assert.NotEqual(parRegime.Fondement, parArticle.Fondement);
    }

    // --- Les anciens noms -----------------------------------------------------

    [Theory]
    [InlineData("LegalExemptionTEE_RME", CodeTvaZero.Tvad, "Tvad")]
    [InlineData("ConventionalExemption", CodeTvaZero.Tvac, "Tvac")]
    public void Un_ancien_nom_decide_encore_mais_se_signale(
        string ancien, CodeTvaZero attendu, string conseille)
    {
        // Casser un paramétrage existant serait pire que le signaler. Mais le
        // laisser passer en silence perpétuerait l'affirmation fautive.
        var decision = Politique(parArticle: new() { ["25SN001"] = ancien }).Decider(Contexte);

        Assert.Equal(attendu, decision.Code);
        Assert.Null(decision.Erreur);
        Assert.NotNull(decision.Avertissement);
        Assert.Contains(ancien, decision.Avertissement);
        Assert.Contains(conseille, decision.Avertissement);
    }

    [Fact]
    public void L_avertissement_sur_TEE_RME_rappelle_ou_ce_regime_se_declare()
    {
        var decision = Politique(
            parArticle: new() { ["25SN001"] = "LegalExemptionTEE_RME" }).Decider(Contexte);

        Assert.Contains("CustomerTaxRegimes", decision.Avertissement);
    }

    [Fact]
    public void Un_ancien_nom_n_invente_pas_de_fondement()
    {
        // Le piège exact : « LegalExemptionTEE_RME » sur un article ne prouve
        // pas que l'article relève de TEE/RME.
        var decision = Politique(
            parArticle: new() { ["25SN001"] = "LegalExemptionTEE_RME" }).Decider(Contexte);

        Assert.Equal(FondementExoneration.NonEtabli, decision.Fondement);
        Assert.NotEqual(FondementExoneration.RegimeAcheteur, decision.Fondement);
    }

    [Theory]
    [InlineData("Tvad")]
    [InlineData("TVAD")]
    [InlineData("Unknown")]
    public void Un_code_ecrit_comme_un_code_ne_declenche_aucun_avertissement(string valeur)
    {
        var decision = Politique(parArticle: new() { ["25SN001"] = valeur }).Decider(Contexte);

        Assert.Null(decision.Avertissement);
    }

    [Fact]
    public void Le_regime_de_l_acheteur_ne_declenche_aucun_avertissement()
    {
        Assert.Null(Politique(regimes: new() { ["4111SOGEL"] = "RME" }).Decider(Contexte).Avertissement);
    }

    // --- Ce qui part réellement à la DGI --------------------------------------

    [Theory]
    [InlineData(CodeTvaZero.Tvac, "TVAC")]
    [InlineData(CodeTvaZero.Tvad, "TVAD")]
    public void Seul_le_code_atteint_le_JSON(CodeTvaZero code, string attendu)
    {
        var ligne = new SageDocumentLine
        {
            Piece = "1", Domaine = 0, Type = 6, Ligne = 1000,
            ArticleReference = "25SN001", Designation = "Sardine",
            Quantite = 1m, PrixUnitaire = 1000m, MontantHT = 1000m,
            CodeTaxe1 = "", Taxe1 = 0m,
        };

        Assert.Equal([attendu], TaxMapping.Read(ligne, code).Taxes);
    }

    [Fact]
    public void Un_code_inconnu_ne_produit_aucune_taxe_et_bloque()
    {
        var ligne = new SageDocumentLine
        {
            Piece = "1", Domaine = 0, Type = 6, Ligne = 1000,
            ArticleReference = "25SN001", Designation = "Sardine",
            Quantite = 1m, PrixUnitaire = 1000m, MontantHT = 1000m,
            CodeTaxe1 = "", Taxe1 = 0m,
        };

        var resultat = TaxMapping.Read(ligne, CodeTvaZero.Inconnu);

        Assert.Empty(resultat.Taxes);
        Assert.True(resultat.RegimeZeroRequis);
    }

    [Fact]
    public void Le_fondement_ne_figure_jamais_dans_les_taxes_envoyees()
    {
        // La DGI ne reçoit qu'un code. Le fondement sert l'audit, pas le JSON.
        foreach (var fondement in Enum.GetValues<FondementExoneration>())
        {
            Assert.DoesNotContain(
                fondement.ToString(),
                string.Join(" ", TaxMapping.Read(new SageDocumentLine
                {
                    Piece = "1", Domaine = 0, Type = 6, Ligne = 1000,
                    ArticleReference = "A", Designation = "A",
                    Quantite = 1m, PrixUnitaire = 1m, MontantHT = 1m,
                    CodeTaxe1 = "", Taxe1 = 0m,
                }, CodeTvaZero.Tvad).Taxes));
        }
    }
}
