using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SageFne.Reader.Batch;
using SageFne.Reader.Certification;
using SageFne.Reader.Configuration;
using SageFne.Reader.Data;
using SageFne.Reader.Fne;
using SageFne.Reader.Mapping;

namespace SageFne.Reader.Tests;

/// <summary>
/// Le câblage se vérifie ici parce qu'il ne se vérifiait nulle part.
/// </summary>
/// <remarks>
/// <c>IFneApiClient</c> a été enregistré sous son seul type concret pendant une
/// version : tous les tests passaient, la compilation aussi, et la commande
/// « envoyer » levait une exception non gérée devant l'utilisateur. Un test qui
/// construit réellement le conteneur l'aurait vu tout de suite.
/// </remarks>
public class CompositionTests
{
    private static ServiceProvider Conteneur(
        string chaineSage = "",
        string? cheminRegistre = null,
        Dictionary<string, string?>? reglages = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(reglages ?? new Dictionary<string, string?>
            {
                ["Fne:Template"] = "B2B",
                ["Fne:PointOfSale"] = "FISH-AFRIC",
                ["Fne:Establishment"] = "FISH-AFRIC",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(journal => journal.SetMinimumLevel(LogLevel.None));
        services.AjouterMiddlewareFne(configuration, chaineSage, cheminRegistre);

        // Les mêmes garde-fous qu'au démarrage réel : une dépendance manquante
        // doit échouer à la construction.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    [Fact]
    public void Le_conteneur_se_construit_et_se_valide()
    {
        // ValidateOnBuild lève si un service enregistré ne peut pas être créé.
        using var conteneur = Conteneur();

        Assert.NotNull(conteneur);
    }

    [Theory]
    [InlineData(typeof(IFneApiClient))]
    [InlineData(typeof(InvoiceSender))]
    [InlineData(typeof(InvoiceBatchReader))]
    [InlineData(typeof(IFneInvoiceMapper))]
    [InlineData(typeof(ISageInvoiceRepository))]
    [InlineData(typeof(ISageTaxInspector))]
    [InlineData(typeof(ICertificationLedger))]
    [InlineData(typeof(FneApiOptions))]
    public void Chaque_service_du_middleware_se_resout(Type service)
    {
        using var conteneur = Conteneur();

        Assert.NotNull(conteneur.GetRequiredService(service));
    }

    [Fact]
    public void Le_client_FNE_se_resout_sous_son_interface()
    {
        // Le défaut exact qui a fait échouer « envoyer » : enregistré sous le
        // type concret, réclamé sous l'interface.
        using var conteneur = Conteneur();

        var client = conteneur.GetRequiredService<IFneApiClient>();

        Assert.IsType<FneApiClient>(client);
        Assert.True(client.Reel);
    }

    [Fact]
    public void L_expediteur_recoit_bien_ses_dependances()
    {
        using var conteneur = Conteneur();

        Assert.NotNull(conteneur.GetRequiredService<InvoiceSender>());
    }

    // --- Les deux configurations possibles ----------------------------------

    [Fact]
    public void Sans_chaine_de_connexion_le_jeu_d_essai_prend_la_place()
    {
        using var conteneur = Conteneur();

        Assert.IsType<DemoSageInvoiceRepository>(conteneur.GetRequiredService<ISageInvoiceRepository>());
        Assert.IsType<DemoCertificationLedger>(conteneur.GetRequiredService<ICertificationLedger>());
    }

    [Fact]
    public void Avec_une_chaine_renseignee_le_depot_SQL_prend_la_place()
    {
        using var conteneur = Conteneur(
            // Authentification Windows : une chaîne valide sans mot de passe.
            // Écrire un faux mot de passe, même dans un test, déclencherait le
            // contrôle de secrets de la CI — et il aurait raison.
            chaineSage: "Server=SRV;Database=HT;Integrated Security=True;",
            cheminRegistre: Path.Combine(Path.GetTempPath(), $"registre-{Guid.NewGuid():N}.json"));

        Assert.IsType<SageInvoiceRepository>(conteneur.GetRequiredService<ISageInvoiceRepository>());
        Assert.IsType<JsonCertificationLedger>(conteneur.GetRequiredService<ICertificationLedger>());
    }

    [Fact]
    public void L_inspecteur_est_la_meme_instance_que_le_depot()
    {
        // Deux rôles, un seul objet : le catalogue de colonnes est mis en cache
        // par instance, et deux instances le reliraient deux fois.
        using var conteneur = Conteneur();

        Assert.Same(
            conteneur.GetRequiredService<ISageInvoiceRepository>(),
            conteneur.GetRequiredService<ISageTaxInspector>());
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("Server=SERVEUR_SQL;Database=HT;", false)]
    [InlineData("Server=X;Pwd=MOT_DE_PASSE;", false)]
    [InlineData("Server=SRV;Database=HT;Integrated Security=True;", true)]
    public void Une_chaine_restee_au_gabarit_ne_compte_pas(string? chaine, bool attendu)
    {
        Assert.Equal(attendu, ServicesMiddleware.ConnexionRenseignee(chaine));
    }

    [Fact]
    public void Le_delai_du_client_HTTP_suit_le_parametrage()
    {
        using var conteneur = Conteneur(reglages: new Dictionary<string, string?>
        {
            ["Fne:TimeoutSeconds"] = "90",
        });

        Assert.Equal(90, conteneur.GetRequiredService<FneApiOptions>().TimeoutSeconds);
    }
}
