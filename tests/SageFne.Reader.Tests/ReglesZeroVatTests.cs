using Microsoft.Extensions.Logging.Abstractions;
using SageFne.Reader.Configuration;
using SageFne.Reader.Mapping;
using SageFne.Reader.Models.Sage;
using SageFne.Reader.Regles;

namespace SageFne.Reader.Tests;

/// <summary>
/// Une règle ne produit son code qu'une fois validée sur une preuve.
/// </summary>
/// <remarks>
/// Le paramétrage disait quel code envoyer, sans dire qui l'avait autorisé ni
/// sur quel document. Or ce code part sur des factures définitives : une
/// déclaration sans preuve doit bloquer, pas certifier.
/// </remarks>
public class RegleZeroVatTests
{
    private static readonly DateTimeOffset Maintenant = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static RegleZeroVat Regle(
        EtatRegle etat = EtatRegle.Validee,
        CodeTvaZero code = CodeTvaZero.Tvad,
        DateTimeOffset? du = null,
        DateTimeOffset? au = null) => new()
    {
        Id = "article-25sn001",
        Portee = PorteeRegle.Article,
        Cle = "25SN001",
        Code = code,
        Etat = etat,
        ValideDu = du,
        ValideAu = au,
    };

    [Fact]
    public void Une_regle_validee_et_datee_s_applique()
    {
        Assert.True(Regle().Applicable(Maintenant));
        Assert.Null(Regle().Empechement(Maintenant));
    }

    [Fact]
    public void Un_brouillon_ne_produit_aucun_code()
    {
        var regle = Regle(etat: EtatRegle.Brouillon);

        Assert.False(regle.Applicable(Maintenant));
        Assert.Contains("brouillon", regle.Empechement(Maintenant));
    }

    [Fact]
    public void Une_regle_revoquee_ne_produit_plus_rien()
    {
        var regle = Regle(etat: EtatRegle.Revoquee) with { Note = "attestation périmée" };

        Assert.False(regle.Applicable(Maintenant));
        Assert.Contains("révoquée", regle.Empechement(Maintenant));
        Assert.Contains("attestation périmée", regle.Empechement(Maintenant));
    }

    [Fact]
    public void Une_regle_validee_sans_code_ne_dit_rien()
    {
        // Une ligne de paramétrage restée à moitié écrite.
        var regle = Regle(code: CodeTvaZero.Inconnu);

        Assert.False(regle.Applicable(Maintenant));
        Assert.Contains("aucun code", regle.Empechement(Maintenant));
    }

    [Fact]
    public void Une_regle_pas_encore_en_vigueur_attend_sa_date()
    {
        var regle = Regle(du: Maintenant.AddDays(1));

        Assert.False(regle.Applicable(Maintenant));
        Assert.Contains("ne prend effet", regle.Empechement(Maintenant));
        Assert.True(regle.Applicable(Maintenant.AddDays(2)));
    }

    [Fact]
    public void Une_regle_perimee_cesse_de_valoir()
    {
        var regle = Regle(au: Maintenant.AddDays(-1));

        Assert.False(regle.Applicable(Maintenant));
        Assert.Contains("cessé de valoir", regle.Empechement(Maintenant));
        Assert.True(regle.Applicable(Maintenant.AddDays(-2)));
    }

    [Fact]
    public void L_identite_ignore_la_casse_de_la_cle()
    {
        Assert.Equal(
            Regle().Identite,
            (Regle() with { Cle = "25sn001" }).Identite);
    }

    [Fact]
    public void L_empreinte_d_un_justificatif_vaut_preuve()
    {
        // La base accepte une référence ou une empreinte. Un affichage qui ne
        // lirait que la référence dirait « aucune preuve » d'une règle qui en
        // porte une — et rien n'inquiète autant qu'une règle validée sans preuve.
        var regle = new RegleZeroVat
        {
            Id = "article-x1",
            Portee = PorteeRegle.Article,
            Cle = "X1",
            EmpreinteJustificatif = "sha256:ab12",
        };

        Assert.NotEqual("", regle.Preuve);
        Assert.Contains("ab12", regle.Preuve);
    }

    [Fact]
    public void Sans_reference_ni_empreinte_il_n_y_a_pas_de_preuve()
    {
        var regle = new RegleZeroVat
        {
            Id = "article-x1",
            Portee = PorteeRegle.Article,
            Cle = "X1",
        };

        Assert.Equal("", regle.Preuve);
    }

    [Fact]
    public void Une_reference_prime_sur_l_empreinte_a_l_affichage()
    {
        var regle = new RegleZeroVat
        {
            Id = "article-x1",
            Portee = PorteeRegle.Article,
            Cle = "X1",
            Reference = "Convention 42",
            EmpreinteJustificatif = "sha256:ab12",
        };

        Assert.Equal("Convention 42", regle.Preuve);
    }
}

/// <summary>
/// Le registre des règles : en ajout seul, et versionné.
/// </summary>
public class RegistreReglesTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), $"regles-{Guid.NewGuid():N}");

    public RegistreReglesTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        if (Directory.Exists(_dossier)) Directory.Delete(_dossier, recursive: true);
    }

    private RegistreRegles Registre() =>
        new(Path.Combine(_dossier, "regles.json"), NullLogger<RegistreRegles>.Instance);

    private static RegleZeroVat Regle(CodeTvaZero code = CodeTvaZero.Tvad) => new()
    {
        Id = "article-25sn001",
        Portee = PorteeRegle.Article,
        Cle = "25SN001",
        Code = code,
        Etat = EtatRegle.Validee,
        Reference = "Réponse DGI",
    };

    [Fact]
    public async Task Un_registre_absent_ne_contient_rien()
    {
        Assert.Empty(await Registre().ToutAsync());
        Assert.Empty(await Registre().CourantesAsync());
    }

    [Fact]
    public async Task La_premiere_ecriture_porte_la_version_1()
    {
        var ecrite = await Registre().AjouterAsync(Regle());

        Assert.Equal(1, ecrite.Version);
        Assert.Equal("article-25sn001 v1", ecrite.Reperage);
    }

    [Fact]
    public async Task Une_modification_cree_une_version_sans_effacer_la_precedente()
    {
        // Des factures sont peut-être parties sous la première.
        var registre = Registre();
        await registre.AjouterAsync(Regle(CodeTvaZero.Tvac));
        await registre.AjouterAsync(Regle(CodeTvaZero.Tvad));

        var toutes = await Registre().ToutAsync();

        Assert.Equal(2, toutes.Count);
        Assert.Equal([1, 2], toutes.Select(regle => regle.Version));
        Assert.Equal(CodeTvaZero.Tvac, toutes[0].Code);
    }

    [Fact]
    public async Task Seule_la_derniere_version_est_courante()
    {
        var registre = Registre();
        await registre.AjouterAsync(Regle(CodeTvaZero.Tvac));
        await registre.AjouterAsync(Regle(CodeTvaZero.Tvad));

        var courante = Assert.Single(await Registre().CourantesAsync()).Value;

        Assert.Equal(2, courante.Version);
        Assert.Equal(CodeTvaZero.Tvad, courante.Code);
    }

    [Fact]
    public async Task Deux_regles_distinctes_se_versionnent_separement()
    {
        var registre = Registre();
        await registre.AjouterAsync(Regle());
        await registre.AjouterAsync(Regle() with { Id = "famille-01", Portee = PorteeRegle.Famille, Cle = "01" });
        await registre.AjouterAsync(Regle());

        var courantes = await Registre().CourantesAsync();

        Assert.Equal(2, courantes.Count);
        Assert.Equal(2, courantes["ARTICLE/25SN001"].Version);
        Assert.Equal(1, courantes["FAMILLE/01"].Version);
    }

    [Fact]
    public async Task L_historique_se_relit_dans_l_ordre()
    {
        var registre = Registre();
        await registre.AjouterAsync(Regle(CodeTvaZero.Tvac));
        await registre.AjouterAsync(Regle(CodeTvaZero.Tvad));

        var historique = await registre.HistoriqueAsync("article-25sn001");

        Assert.Equal([1, 2], historique.Select(regle => regle.Version));
    }

    [Fact]
    public async Task Un_registre_illisible_leve_au_lieu_de_passer_pour_vide()
    {
        // Même règle que pour les certifications : illisible n'est pas vide.
        await File.WriteAllTextAsync(Path.Combine(_dossier, "regles.json"), "{ tronqué");

        await Assert.ThrowsAsync<RegistreReglesIllisibleException>(() => Registre().ToutAsync());
    }

    [Fact]
    public async Task Une_regle_survit_a_l_aller_retour_JSON()
    {
        var origine = Regle() with
        {
            Fondement = FondementExoneration.ExonerationLegaleProduit,
            ValideePar = "DGI",
            ValideeLe = new DateTimeOffset(2026, 9, 5, 10, 0, 0, TimeSpan.Zero),
            EmpreinteJustificatif = "abc123",
            Motif = "réponse écrite",
            ValideDu = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };

        await Registre().AjouterAsync(origine);
        var relue = Assert.Single(await Registre().ToutAsync());

        Assert.Equal(origine.Code, relue.Code);
        Assert.Equal(origine.Fondement, relue.Fondement);
        Assert.Equal(origine.Etat, relue.Etat);
        Assert.Equal(origine.ValideePar, relue.ValideePar);
        Assert.Equal(origine.ValideeLe, relue.ValideeLe);
        Assert.Equal(origine.EmpreinteJustificatif, relue.EmpreinteJustificatif);
        Assert.Equal(origine.ValideDu, relue.ValideDu);
    }

    [Fact]
    public async Task L_etat_et_le_code_s_ecrivent_en_toutes_lettres()
    {
        await Registre().AjouterAsync(Regle());

        var json = await File.ReadAllTextAsync(Path.Combine(_dossier, "regles.json"));

        Assert.Contains("\"Validee\"", json);
        Assert.Contains("\"Tvad\"", json);
    }
}

/// <summary>
/// La politique adossée au registre, et le sort du paramétrage hérité.
/// </summary>
public class RegistreZeroVatPolicyTests
{
    private static readonly DateTimeOffset Maintenant = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly ZeroVatContexte Contexte = new("25SN001", "01", "4111SOGEL");

    private static RegistreZeroVatPolicy Politique(
        IEnumerable<RegleZeroVat>? regles = null,
        ZeroVatOptions? heritage = null) =>
        new(
            (regles ?? []).ToDictionary(regle => regle.Identite, StringComparer.OrdinalIgnoreCase),
            heritage ?? new ZeroVatOptions(),
            Maintenant);

    private static RegleZeroVat Regle(
        PorteeRegle portee, string cle, CodeTvaZero code = CodeTvaZero.Tvad,
        EtatRegle etat = EtatRegle.Validee,
        FondementExoneration fondement = FondementExoneration.NonEtabli) => new()
    {
        Id = $"{portee}-{cle}".ToLowerInvariant(),
        Portee = portee,
        Cle = cle,
        Code = code,
        Etat = etat,
        Fondement = fondement,
        Reference = "preuve",
    };

    [Fact]
    public void Sans_regle_ni_parametrage_la_ligne_est_bloquee()
    {
        var decision = Politique().Decider(Contexte);

        Assert.Equal(CodeTvaZero.Inconnu, decision.Code);
        Assert.Equal("aucune règle applicable", decision.Origine);
    }

    [Fact]
    public void Une_regle_validee_produit_son_code_et_se_nomme()
    {
        var decision = Politique([Regle(PorteeRegle.Article, "25SN001")]).Decider(Contexte);

        Assert.Equal(CodeTvaZero.Tvad, decision.Code);
        Assert.Contains("article 25SN001", decision.Origine);
        Assert.Contains("v1", decision.Origine);
    }

    [Fact]
    public void Un_brouillon_bloque_et_dit_pourquoi()
    {
        var decision = Politique(
            [Regle(PorteeRegle.Article, "25SN001", etat: EtatRegle.Brouillon)]).Decider(Contexte);

        Assert.Equal(CodeTvaZero.Inconnu, decision.Code);
        Assert.Contains("brouillon", decision.Erreur);
    }

    [Fact]
    public void Un_brouillon_ne_laisse_pas_la_main_au_niveau_suivant()
    {
        // Sinon une règle d'article en attente de validation serait silencieusement
        // remplacée par celle de la famille, et la facture partirait sous un code
        // que personne n'a arrêté pour ce produit.
        var decision = Politique([
            Regle(PorteeRegle.Article, "25SN001", CodeTvaZero.Tvac, EtatRegle.Brouillon),
            Regle(PorteeRegle.Famille, "01", CodeTvaZero.Tvad),
        ]).Decider(Contexte);

        Assert.Equal(CodeTvaZero.Inconnu, decision.Code);
        Assert.Contains("article", decision.Erreur);
    }

    [Fact]
    public void Le_regime_de_l_acheteur_reste_prioritaire()
    {
        var decision = Politique([
            Regle(PorteeRegle.Article, "25SN001", CodeTvaZero.Tvac),
            Regle(PorteeRegle.RegimeAcheteur, "4111SOGEL", CodeTvaZero.Tvad,
                fondement: FondementExoneration.RegimeAcheteur),
        ]).Decider(Contexte);

        Assert.Equal(CodeTvaZero.Tvad, decision.Code);
        Assert.Equal(FondementExoneration.RegimeAcheteur, decision.Fondement);
    }

    [Fact]
    public void L_ordre_article_famille_client_dossier_est_conserve()
    {
        Assert.Contains("article", Politique([
            Regle(PorteeRegle.Article, "25SN001"),
            Regle(PorteeRegle.Famille, "01"),
            Regle(PorteeRegle.Client, "4111SOGEL"),
            Regle(PorteeRegle.Dossier, ""),
        ]).Decider(Contexte).Origine);

        Assert.Contains("famille", Politique([
            Regle(PorteeRegle.Famille, "01"),
            Regle(PorteeRegle.Client, "4111SOGEL"),
        ]).Decider(Contexte).Origine);

        Assert.Contains("dossier", Politique([Regle(PorteeRegle.Dossier, "")])
            .Decider(Contexte).Origine);
    }

    [Fact]
    public void Le_fondement_de_la_regle_est_repris_tel_quel()
    {
        var decision = Politique([
            Regle(PorteeRegle.Article, "25SN001",
                fondement: FondementExoneration.ExonerationLegaleProduit),
        ]).Decider(Contexte);

        Assert.Equal(FondementExoneration.ExonerationLegaleProduit, decision.Fondement);
    }

    // --- Le paramétrage hérité ------------------------------------------------

    [Fact]
    public void Une_declaration_de_parametrage_ne_certifie_plus_rien()
    {
        // Elle dit quel code envoyer sans dire qui l'a autorisé. Sur une facture
        // définitive, cela ne suffit pas.
        var decision = Politique(heritage: new ZeroVatOptions
        {
            ByArticle = new(StringComparer.OrdinalIgnoreCase) { ["25SN001"] = "Tvad" },
        }).Decider(Contexte);

        Assert.Equal(CodeTvaZero.Inconnu, decision.Code);
        Assert.NotNull(decision.Erreur);
        Assert.Contains("aucune règle validée", decision.Erreur);
    }

    [Fact]
    public void Le_message_nomme_la_commande_qui_promeut_la_declaration()
    {
        var decision = Politique(heritage: new ZeroVatOptions
        {
            ByArticle = new(StringComparer.OrdinalIgnoreCase) { ["25SN001"] = "Tvad" },
        }).Decider(Contexte);

        Assert.Contains("zero-vat-regle article 25SN001", decision.Erreur);
    }

    [Fact]
    public void Un_regime_declare_au_parametrage_bloque_aussi()
    {
        var decision = Politique(heritage: new ZeroVatOptions
        {
            CustomerTaxRegimes = new(StringComparer.OrdinalIgnoreCase) { ["4111SOGEL"] = "RME" },
        }).Decider(Contexte);

        Assert.Equal(CodeTvaZero.Inconnu, decision.Code);
        Assert.Contains("zero-vat-regle client 4111SOGEL", decision.Erreur);
    }

    [Fact]
    public void Une_regle_du_registre_l_emporte_sur_le_parametrage()
    {
        var decision = Politique(
            [Regle(PorteeRegle.Article, "25SN001", CodeTvaZero.Tvac)],
            new ZeroVatOptions
            {
                ByArticle = new(StringComparer.OrdinalIgnoreCase) { ["25SN001"] = "Tvad" },
            }).Decider(Contexte);

        Assert.Equal(CodeTvaZero.Tvac, decision.Code);
        Assert.Null(decision.Erreur);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("")]
    public void Une_declaration_vide_n_est_pas_une_declaration(string valeur)
    {
        var decision = Politique(heritage: new ZeroVatOptions
        {
            ByArticle = new(StringComparer.OrdinalIgnoreCase) { ["25SN001"] = valeur },
        }).Decider(Contexte);

        Assert.Equal("aucune règle applicable", decision.Origine);
        Assert.Null(decision.Erreur);
    }

    // --- Ce que le registre ne fait jamais ------------------------------------

    [Fact]
    public void Une_regle_ne_detaxe_jamais_une_ligne()
    {
        // La politique n'est consultée que sur une ligne à 0 %. Le vérifier ici
        // aussi : une règle TVAD ne doit pas pouvoir effacer un taux.
        var ligne = new SageDocumentLine
        {
            Piece = "1", Domaine = 0, Type = 6, Ligne = 1000,
            ArticleReference = "25SN001", Designation = "Sardine",
            Quantite = 1m, PrixUnitaire = 1000m, MontantHT = 1000m,
            CodeTaxe1 = "TVA", Taxe1 = 18m,
        };

        var decision = Politique([Regle(PorteeRegle.Article, "25SN001")]).Decider(Contexte);

        Assert.Equal(["TVA"], TaxMapping.Read(ligne, decision.Code).Taxes);
    }
}
