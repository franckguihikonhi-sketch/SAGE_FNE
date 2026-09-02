using Microsoft.Extensions.DependencyInjection;
using SageFne.Agent.Sante;
using SageFne.Core.Configuration;

namespace SageFne.Agent.Configuration;

/// <summary>
/// Le câblage propre à l'agent, hors du Program.
/// </summary>
/// <remarks>
/// Ce qui vit dans une lambda de Program.cs n'est atteignable par aucun test :
/// des instructions de haut niveau ne s'appellent pas. C'est là que la sonde
/// lisait un <c>IOptions&lt;FneApiOptions&gt;</c> que personne ne configure, et
/// répondait « INJOIGNABLE » sans rien éprouver — pendant des semaines, sans
/// qu'aucun des tests verts n'ait la moindre chance de le voir.
/// </remarks>
public static class ServicesAgent
{
    /// <summary>Battement de cœur et sonde de joignabilité.</summary>
    /// <param name="delaiSonde">Au-delà, la plateforme est réputée sans réponse.</param>
    public static IServiceCollection AjouterSante(
        this IServiceCollection services, TimeSpan delaiSonde)
    {
        services.AddSingleton<IPublicationHeartbeat, HeartbeatJournal>();

        // FneApiOptions, l'instance liée par AjouterMiddlewareFne — la même que
        // lit le CLI. Pas un IOptions<> : rien ne l'alimente.
        services.AddSingleton<ISondeReseau>(fournisseur =>
            SondeReseau.Pour(
                fournisseur.GetRequiredService<FneApiOptions>().BaseUrl, delaiSonde));

        return services;
    }
}
