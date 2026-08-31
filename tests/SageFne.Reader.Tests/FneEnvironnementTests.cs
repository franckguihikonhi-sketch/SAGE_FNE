using SageFne.Reader.Configuration;

namespace SageFne.Reader.Tests;

/// <summary>
/// Envoyer une facture d'essai vers la plateforme réelle la certifierait pour
/// de vrai, et une certification ne se corrige que par un avoir. Le garde-fou
/// qui l'empêche mérite d'être tenu par des tests.
/// </summary>
public class FneEnvironnementTests
{
    private static FneApiOptions Options(
        string url,
        FneEnvironment environnement = FneEnvironment.Test,
        string cle = "cle-de-test-123456789",
        List<string>? marqueurs = null) => new()
    {
        BaseUrl = url,
        ApiKey = cle,
        Environment = environnement,
        TestHostMarkers = marqueurs ?? [],
    };

    [Fact]
    public void L_environnement_par_defaut_est_TEST()
    {
        // Un défaut de production ferait certifier pour de vrai une
        // configuration oubliée.
        Assert.Equal(FneEnvironment.Test, new FneApiOptions().Environment);
        Assert.True(new FneApiOptions().EstTest);
    }

    [Theory]
    [InlineData("https://api-test.dgi.gouv.ci")]
    [InlineData("https://sandbox.dgi.gouv.ci")]
    [InlineData("https://preprod.dgi.gouv.ci")]
    [InlineData("https://recette.dgi.gouv.ci")]
    [InlineData("https://uat.dgi.gouv.ci")]
    [InlineData("http://localhost:5000")]
    public void En_TEST_une_adresse_d_essai_passe(string url)
    {
        Assert.Null(Options(url).Verifier());
        Assert.True(Options(url).EstConfigure);
    }

    [Theory]
    [InlineData("https://api.dgi.gouv.ci")]
    [InlineData("https://fne.dgi.gouv.ci")]
    [InlineData("https://prod.dgi.gouv.ci")]
    [InlineData("https://api.production.example")]
    public void En_TEST_une_adresse_de_production_est_refusee(string url)
    {
        var refus = Options(url).Verifier();

        Assert.NotNull(refus);
        Assert.Contains("TEST", refus);
        Assert.Contains("certifierait pour de vrai", refus);
        Assert.False(Options(url).EstConfigure);
    }

    [Fact]
    public void Le_refus_est_une_liste_d_autorisation_pas_d_interdiction()
    {
        // Un hôte inconnu ne passe pas : c'est ainsi qu'une adresse de
        // production inconnue du code finirait appelée depuis une config de test.
        Assert.NotNull(Options("https://quelque-chose-de-nouveau.example").Verifier());
    }

    [Fact]
    public void En_PRODUCTION_le_garde_fou_ne_bloque_plus()
    {
        // Le choix est alors explicite et assumé.
        Assert.Null(Options("https://api.dgi.gouv.ci", FneEnvironment.Production).Verifier());
    }

    [Fact]
    public void Les_marqueurs_se_completent_sans_se_dedoubler()
    {
        // Le binder de configuration ajoute à la liste au lieu de la remplacer :
        // sans normalisation, chaque marqueur apparaissait deux fois.
        var options = Options("https://essai.dgi.gouv.ci", marqueurs: ["essai", "essai", "ESSAI"]);

        Assert.Null(options.Verifier());
        Assert.Single(options.Marqueurs);
    }

    [Fact]
    public void Sans_marqueur_configure_les_defauts_s_appliquent()
    {
        Assert.Contains("sandbox", new FneApiOptions().Marqueurs);
        Assert.Equal(8, new FneApiOptions().Marqueurs.Count);
    }

    [Fact]
    public void Une_adresse_en_clair_est_refusee()
    {
        // La clé d'API ne doit pas voyager en HTTP.
        var refus = Options("http://api-test.dgi.gouv.ci").Verifier();

        Assert.NotNull(refus);
        Assert.Contains("HTTPS", refus);
    }

    [Fact]
    public void Localhost_reste_autorise_en_clair()
    {
        // Un bouchon local n'est pas un risque de fuite.
        Assert.Null(Options("http://localhost:5000").Verifier());
    }

    [Fact]
    public void Une_adresse_relative_est_refusee()
    {
        Assert.NotNull(Options("/external").Verifier());
    }

    [Fact]
    public void Sans_cle_la_configuration_n_est_pas_complete()
    {
        var options = Options("https://api-test.dgi.gouv.ci", cle: "");

        Assert.False(options.CleRenseignee);
        Assert.False(options.EstConfigure);
        // L'adresse, elle, reste valide : les deux manques se disent séparément.
        Assert.Null(options.Verifier());
    }

    [Fact]
    public void Un_gabarit_non_remplace_ne_compte_pas_comme_une_adresse()
    {
        Assert.False(Options("https://A_COMPLETER").UrlRenseignee);
    }

    [Fact]
    public void La_cle_absente_se_dit_sans_reveler_de_longueur()
    {
        Assert.Equal("— absente —", new FneApiOptions().CleMasquee());
    }

    [Fact]
    public void Une_cle_courte_ne_laisse_rien_filtrer()
    {
        var masquee = new FneApiOptions { ApiKey = "abcdefgh" }.CleMasquee();

        Assert.Equal("••••••••", masquee);
        Assert.DoesNotContain("a", masquee);
    }
}
