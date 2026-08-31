using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SageFne.Reader.Batch;
using SageFne.Reader.Certification;
using SageFne.Reader.Data;
using SageFne.Reader.Fne;
using SageFne.Reader.Mapping;

namespace SageFne.Reader.Configuration;

/// <summary>
/// Le câblage du middleware, en un seul endroit.
/// </summary>
/// <remarks>
/// Extrait de <c>Program</c> pour une raison précise : tant qu'il vivait dans
/// des instructions de haut niveau, aucun test ne pouvait construire le
/// conteneur. Une interface enregistrée sous son type concret passait alors
/// toutes les vérifications et n'échouait qu'à l'exécution, devant
/// l'utilisateur. C'est arrivé avec <see cref="IFneApiClient"/>.
/// </remarks>
public static class ServicesMiddleware
{
    /// <param name="chaineSage">
    /// Chaîne de connexion Sage. Vide ou laissée au gabarit : le jeu d'essai
    /// prend la place, et rien ne parle à SQL Server.
    /// </param>
    /// <param name="cheminRegistre">
    /// Fichier du registre des certifications. Null : registre en mémoire.
    /// </param>
    public static IServiceCollection AjouterMiddlewareFne(
        this IServiceCollection services,
        IConfiguration configuration,
        string chaineSage,
        string? cheminRegistre)
    {
        services.Configure<FneOptions>(configuration.GetSection(FneOptions.Section));

        // L'API de la DGI, liée sur la même section que le reste : la clé se
        // pose en « Fne:ApiKey », dans les secrets utilisateur et nulle part
        // ailleurs.
        var api = new FneApiOptions();
        configuration.GetSection(FneOptions.Section).Bind(api);
        services.AddSingleton(api);

        // Sous l'interface, et pas seulement sous le type concret : c'est
        // l'interface que réclame InvoiceSender.
        services.AddHttpClient<IFneApiClient, FneApiClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(api.TimeoutSeconds, 5, 300));
        });

        services.AddSingleton<InvoiceSender>();
        services.AddSingleton<IFneInvoiceMapper, FneInvoiceMapper>();
        services.AddSingleton<InvoiceBatchReader>();

        if (ConnexionRenseignee(chaineSage))
        {
            services.AddSingleton<ISageInvoiceRepository>(fournisseur =>
                new SageInvoiceRepository(
                    chaineSage, fournisseur.GetRequiredService<ILogger<SageInvoiceRepository>>()));
        }
        else
        {
            services.AddSingleton<ISageInvoiceRepository, DemoSageInvoiceRepository>();
        }

        // Les deux dépôts savent aussi explorer : même instance, deux rôles.
        services.AddSingleton<ISageTaxInspector>(fournisseur =>
            (ISageTaxInspector)fournisseur.GetRequiredService<ISageInvoiceRepository>());

        // Le registre des certifications vit hors de Sage : la base y est en
        // lecture seule, et rien n'y prévoit de zone pour la référence FNE.
        if (cheminRegistre is not null)
        {
            services.AddSingleton<ICertificationLedger>(fournisseur =>
                new JsonCertificationLedger(
                    cheminRegistre, fournisseur.GetRequiredService<ILogger<JsonCertificationLedger>>()));
        }
        else
        {
            services.AddSingleton<ICertificationLedger, DemoCertificationLedger>();
        }

        return services;
    }

    /// <summary>
    /// Une chaîne restée au gabarit n'est pas une chaîne : mieux vaut le jeu
    /// d'essai, qui se déclare, qu'une tentative de connexion vers « SERVEUR_SQL ».
    /// </summary>
    public static bool ConnexionRenseignee(string? chaine) =>
        !string.IsNullOrWhiteSpace(chaine)
        && !chaine.Contains("SERVEUR_SQL", StringComparison.OrdinalIgnoreCase)
        && !chaine.Contains("MOT_DE_PASSE", StringComparison.OrdinalIgnoreCase);
}
