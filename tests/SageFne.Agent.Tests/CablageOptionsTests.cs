using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SageFne.Agent.Configuration;
using SageFne.Agent.Sante;
using SageFne.Core.Configuration;

namespace SageFne.Agent.Tests;

/// <summary>
/// De quel objet l'agent tire son paramétrage FNE.
/// </summary>
/// <remarks>
/// Sur le premier poste réel, le journal a dit « Plateforme FNE : INJOIGNABLE »
/// pendant que <c>Test-NetConnection</c> ouvrait la connexion sans peine. La
/// sonde n'éprouvait rien : elle lisait un <c>IOptions&lt;FneApiOptions&gt;</c>
/// que personne ne configure, donc un objet neuf, <c>BaseUrl</c> vide, et
/// retombait sur la sonde qui dit toujours non.
///
/// Le même objet servait à nommer l'environnement dans le battement. Comme
/// <c>FneApiOptions.Environment</c> vaut <c>Test</c> par défaut, un agent
/// configuré en production aurait battu « env=TEST » — un tableau de bord aurait
/// montré comme inoffensif un agent qui certifie pour de vrai.
///
/// Les deux fautes échouaient du bon côté, ce qui les a rendues invisibles : ne
/// rien envoyer, et se dire en essai. C'est précisément pourquoi elles avaient
/// besoin d'un test.
/// </remarks>
public class CablageOptionsTests
{
    private static ServiceProvider Cabler(params (string Cle, string Valeur)[] reglages)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(reglages.Select(r =>
                new KeyValuePair<string, string?>(r.Cle, r.Valeur)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.Section));
        services.AjouterMiddlewareFne(configuration, chaineSage: "", cheminRegistre: null);
        services.AjouterSante(TimeSpan.FromMilliseconds(50));
        services.AddSingleton<ServiceSurveillance>();
        return services.BuildServiceProvider();
    }

    /// <summary>Recueille le battement au lieu de l'écrire.</summary>
    private sealed class BattementRecueilli : IPublicationHeartbeat
    {
        public Heartbeat? Dernier { get; private set; }

        public Task PublierAsync(Heartbeat battement, CancellationToken cancellation = default)
        {
            Dernier = battement;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void L_instance_liee_porte_la_configuration()
    {
        using var fournisseur = Cabler(
            ("Fne:BaseUrl", "http://54.247.95.108/ws"),
            ("Fne:Environment", "Production"));

        var api = fournisseur.GetRequiredService<FneApiOptions>();

        Assert.Equal("http://54.247.95.108/ws", api.BaseUrl);
        Assert.False(api.EstTest);
    }

    [Fact]
    public void Un_IOptions_ne_porte_rien_car_personne_ne_le_configure()
    {
        // Le piège, écrit noir sur blanc. Ce n'est pas un comportement souhaité :
        // c'est le comportement réel, et tant qu'il est réel, tout code de
        // l'agent qui passerait par IOptions travaillerait sur du vide sans que
        // rien ne le signale.
        using var fournisseur = Cabler(
            ("Fne:BaseUrl", "http://54.247.95.108/ws"),
            ("Fne:Environment", "Production"));

        var parOptions = fournisseur.GetRequiredService<IOptions<FneApiOptions>>().Value;

        Assert.Equal("", parOptions.BaseUrl);
        Assert.True(parOptions.EstTest);
    }

    [Fact]
    public void La_sonde_construite_depuis_la_configuration_vise_la_plateforme()
    {
        using var fournisseur = Cabler(("Fne:BaseUrl", "http://54.247.95.108/ws"));
        var api = fournisseur.GetRequiredService<FneApiOptions>();

        var sonde = SondeReseau.Pour(api.BaseUrl, TimeSpan.FromSeconds(5));

        var tcp = Assert.IsType<SondeTcp>(sonde);
        Assert.Equal("54.247.95.108:80", tcp.Cible);
    }

    [Fact]
    public async Task Sans_adresse_la_sonde_dit_non_sans_rien_eprouver()
    {
        // Une sonde qui répondrait « oui » par défaut ferait entrer l'agent dans
        // le chemin d'envoi avec une configuration vide.
        var sonde = SondeReseau.Pour("", TimeSpan.FromSeconds(5));

        Assert.IsType<SondeFigee>(sonde);
        Assert.False(await sonde.JoignableAsync());
    }

    [Theory]
    [InlineData("pas-une-adresse")]
    [InlineData("/external/invoices/sign")]
    [InlineData(null)]
    public void Une_adresse_inexploitable_ne_devient_pas_une_cible(string? adresse)
    {
        Assert.IsType<SondeFigee>(SondeReseau.Pour(adresse, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Le_conteneur_reel_rend_une_sonde_qui_vise_la_plateforme_configuree()
    {
        // Le test qui manquait. Le précédent éprouvait SondeReseau.Pour, que
        // personne ne contestait ; il laissait passer la faute, qui était dans
        // le câblage. Réintroduire IOptions dans AjouterSante doit faire tomber
        // celui-ci.
        using var fournisseur = Cabler(("Fne:BaseUrl", "http://54.247.95.108/ws"));

        var sonde = fournisseur.GetRequiredService<ISondeReseau>();

        var tcp = Assert.IsType<SondeTcp>(sonde);
        Assert.Equal("54.247.95.108:80", tcp.Cible);
    }

    [Fact]
    public async Task Un_agent_configure_en_production_ne_bat_pas_env_TEST()
    {
        // La plus dangereuse des deux fautes : FneApiOptions.Environment vaut
        // Test par défaut, si bien qu'un agent lisant un IOptions non configuré
        // battait « env=TEST » tout en certifiant pour de vrai. Un tableau de
        // bord l'aurait montré inoffensif.
        var recueil = new BattementRecueilli();
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Fne:BaseUrl"] = "https://exemple-production.invalid/ws",
                ["Fne:Environment"] = "Production",
            })
            .Build();

        services.AddLogging();
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.Section));
        services.AjouterMiddlewareFne(configuration, chaineSage: "", cheminRegistre: null);
        services.AjouterSante(TimeSpan.FromMilliseconds(50));
        services.AddSingleton<IPublicationHeartbeat>(recueil);
        services.AddSingleton<ServiceSurveillance>();

        using var fournisseur = services.BuildServiceProvider();
        await fournisseur.GetRequiredService<ServiceSurveillance>().VerifierAsync();

        Assert.NotNull(recueil.Dernier);
        Assert.Equal("PRODUCTION", recueil.Dernier!.Environnement);
        Assert.Contains("env=PRODUCTION", recueil.Dernier.ToString());
    }
}
