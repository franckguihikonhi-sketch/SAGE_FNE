using SageFne.Core.Configuration;

namespace SageFne.Core.Tests;

/// <summary>
/// La DGI publie <c>http://54.247.95.108/ws</c> pour son environnement d'essai :
/// du HTTP clair, sur une IP nue. Ce n'est pas défendable en général — la clé y
/// voyage lisible de tout équipement traversé. D'où une exception nominative
/// plutôt qu'une règle : HTTP n'est jamais autorisé en tant que tel, cette
/// adresse-ci l'est.
/// </summary>
public class FneEnvironnementTests
{
    private const string UrlEssai = "http://54.247.95.108/ws";

    private static FneApiOptions Options(
        string url,
        FneEnvironment environnement = FneEnvironment.Test,
        string cle = "cle-de-test-123456789",
        List<string>? autorisees = null) => new()
    {
        BaseUrl = url,
        ApiKey = cle,
        Environment = environnement,
        TestAllowedUrls = autorisees ?? [],
    };

    // --- Ce qui passe -------------------------------------------------------

    [Fact]
    public void L_adresse_d_essai_officielle_est_acceptee()
    {
        var options = Options(UrlEssai);

        Assert.Null(options.Verifier());
        Assert.True(options.EstConfigure);
        Assert.Equal(
            "http://54.247.95.108/ws/external/invoices/sign",
            options.AdresseSignature().ToString());
    }

    [Fact]
    public void L_adresse_sans_le_chemin_est_normalisee_vers_l_officielle()
    {
        // Sans le /ws, l'adresse de signature aurait été
        // http://54.247.95.108/external/invoices/sign — fausse, et l'échec
        // aurait été incompréhensible.
        var options = Options("http://54.247.95.108");

        Assert.Null(options.Verifier());
        Assert.Equal(UrlEssai, options.BaseUrlEffective);
        Assert.Equal(
            "http://54.247.95.108/ws/external/invoices/sign",
            options.AdresseSignature().ToString());
    }

    [Theory]
    [InlineData("http://54.247.95.108/ws/")]
    [InlineData("HTTP://54.247.95.108/ws")]
    [InlineData("  http://54.247.95.108/ws  ")]
    [InlineData("http://54.247.95.108:80/ws")]
    public void Les_ecritures_equivalentes_sont_acceptees(string url)
    {
        Assert.Null(Options(url).Verifier());
    }

    // --- Ce qui est refusé --------------------------------------------------

    [Theory]
    [InlineData("http://54.247.95.109/ws")]      // une IP voisine
    [InlineData("http://54.247.95.108:8080/ws")] // un autre port
    [InlineData("http://autre-serveur/ws")]
    [InlineData("http://localhost:5000")]
    public void Toute_autre_adresse_HTTP_est_refusee(string url)
    {
        // HTTP n'est pas autorisé « en général » : seule l'adresse déclarée l'est.
        var refus = Options(url).Verifier();

        Assert.NotNull(refus);
        Assert.Contains("TEST", refus);
        Assert.False(Options(url).EstConfigure);
    }

    [Theory]
    [InlineData("https://api-test.dgi.gouv.ci")]
    [InlineData("https://sandbox.dgi.gouv.ci")]
    [InlineData("https://api.dgi.gouv.ci")]
    public void Une_adresse_HTTPS_inconnue_est_refusee_aussi(string url)
    {
        // Le protocole ne rachète pas l'adresse : la liste est exacte.
        var refus = Options(url).Verifier();

        Assert.NotNull(refus);
        Assert.Contains("n'en fait pas partie", refus);
    }

    [Fact]
    public void Le_refus_nomme_les_adresses_admises()
    {
        var refus = Options("https://ailleurs.example").Verifier();

        Assert.NotNull(refus);
        Assert.Contains(UrlEssai, refus);
        Assert.Contains("Fne:TestAllowedUrls", refus);
    }

    [Theory]
    [InlineData("pas une adresse")]
    [InlineData("/external/invoices")]
    [InlineData("ftp://54.247.95.108/ws")]
    public void Ce_qui_n_est_pas_une_adresse_http_est_refuse(string url)
    {
        Assert.NotNull(Options(url).Verifier());
    }

    // --- Production ---------------------------------------------------------

    [Fact]
    public void En_production_le_HTTP_n_a_aucune_exception()
    {
        // L'exception vaut pour la plateforme d'essai, pas au-delà.
        var refus = Options(UrlEssai, FneEnvironment.Production).Verifier();

        Assert.NotNull(refus);
        Assert.Contains("HTTPS", refus);
        Assert.Contains("en clair", refus);
    }

    [Fact]
    public void En_production_une_adresse_HTTPS_passe_sans_liste()
    {
        Assert.Null(Options("https://api.dgi.gouv.ci", FneEnvironment.Production).Verifier());
    }

    [Fact]
    public void L_environnement_par_defaut_est_TEST()
    {
        // Un défaut de production ferait certifier pour de vrai une
        // configuration oubliée.
        Assert.Equal(FneEnvironment.Test, new FneApiOptions().Environment);
    }

    // --- La liste elle-même -------------------------------------------------

    [Fact]
    public void La_liste_par_defaut_ne_contient_que_l_adresse_de_la_DGI()
    {
        var seule = Assert.Single(new FneApiOptions().AdressesAutorisees);

        Assert.Equal(UrlEssai, seule);
    }

    [Fact]
    public void Une_adresse_declaree_explicitement_est_admise()
    {
        // Un bouchon local, par exemple. L'ajout est un acte délibéré.
        var options = Options("http://localhost:5000", autorisees: ["http://localhost:5000"]);

        Assert.Null(options.Verifier());
    }

    [Fact]
    public void La_liste_configuree_remplace_la_valeur_par_defaut()
    {
        // Le binder ajoute au lieu de remplacer : la liste part donc vide, et
        // le défaut est servi par la propriété calculée.
        var options = Options(UrlEssai, autorisees: ["http://localhost:5000"]);

        Assert.NotNull(options.Verifier());
        Assert.Single(options.AdressesAutorisees);
    }

    [Fact]
    public void Les_doublons_de_configuration_ne_se_repetent_pas()
    {
        var options = Options(UrlEssai, autorisees: [UrlEssai, UrlEssai + "/", "HTTP://54.247.95.108/ws"]);

        Assert.Single(options.AdressesAutorisees);
        Assert.Null(options.Verifier());
    }

    [Fact]
    public void L_adresse_en_clair_se_signale()
    {
        Assert.True(Options(UrlEssai).EnClair);
        Assert.False(Options("https://api.dgi.gouv.ci", FneEnvironment.Production).EnClair);
    }

    // --- La clé -------------------------------------------------------------

    [Fact]
    public void Sans_cle_la_preparation_d_envoi_est_refusee()
    {
        var options = Options(UrlEssai, cle: "");

        Assert.False(options.CleRenseignee);
        Assert.False(options.EstConfigure);
        // L'adresse reste valide : les deux manques se disent séparément.
        Assert.Null(options.Verifier());
    }

    [Fact]
    public void La_cle_n_apparait_jamais_en_clair()
    {
        var options = Options(UrlEssai, cle: "sk-test-1234567890-abcdef");

        var masquee = options.CleMasquee();

        Assert.DoesNotContain(options.ApiKey, masquee);
        Assert.DoesNotContain("1234567890", masquee);
        Assert.StartsWith("sk-t", masquee);
        Assert.EndsWith("cdef", masquee);
    }

    [Fact]
    public void Une_cle_courte_ne_laisse_rien_filtrer()
    {
        Assert.Equal("••••••••", new FneApiOptions { ApiKey = "abcdefgh" }.CleMasquee());
    }

    [Fact]
    public void La_cle_absente_se_dit_sans_reveler_de_longueur()
    {
        Assert.Equal("— absente —", new FneApiOptions().CleMasquee());
    }

    [Fact]
    public void La_normalisation_rend_null_sur_ce_qui_n_est_pas_une_adresse()
    {
        Assert.Null(FneApiOptions.Normaliser(""));
        Assert.Null(FneApiOptions.Normaliser("   "));
        Assert.Null(FneApiOptions.Normaliser(null));
        Assert.Null(FneApiOptions.Normaliser("ftp://x/y"));
        Assert.Equal("http://54.247.95.108/ws", FneApiOptions.Normaliser("http://54.247.95.108/ws/"));
    }
}
