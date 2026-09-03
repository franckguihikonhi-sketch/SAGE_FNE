using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SageFne.Agent.Sante;
using SageFne.Agent.Surveillance;
using SageFne.Agent.Tableau;
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
    /// <summary>
    /// Tout le câblage propre à l'agent, en un seul appel.
    /// </summary>
    /// <remarks>
    /// Un seul point d'entrée, et c'est délibéré. Tant que ce câblage était
    /// éparpillé en méthodes qu'il fallait penser à appeler toutes, un appelant
    /// pouvait en oublier une : le service se construisait alors sans sa mémoire
    /// de stabilité, ou avec une sonde que rien n'alimentait. C'est exactement
    /// ainsi que la sonde a répondu « INJOIGNABLE » pendant des semaines sur un
    /// poste où la plateforme répondait.
    ///
    /// Program.cs et les tests appellent donc la même méthode. Ce que le test
    /// construit est ce que le service construit.
    /// </remarks>
    /// <param name="delaiSonde">Au-delà, la plateforme est réputée sans réponse.</param>
    public static IServiceCollection AjouterAgent(
        this IServiceCollection services, TimeSpan delaiSonde)
    {
        services.AjouterSante(delaiSonde);
        services.AjouterMemoiresAgent();
        services.AjouterTableau();
        return services;
    }

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

    /// <summary>
    /// Les deux mémoires de l'agent, partagées par tout ce qui décide.
    /// </summary>
    /// <remarks>
    /// Elles étaient jusqu'ici des variables locales du tour de garde. Cela
    /// suffisait tant qu'un seul composant décidait ; le tableau de bord en est
    /// un second. S'il construisait les siennes, l'écran annoncerait un compte à
    /// rebours de stabilité qui repartirait de zéro à chaque rafraîchissement,
    /// et une attente après refus qui n'aurait rien à voir avec celle que
    /// l'agent applique. Deux vérités pour un même fait, et c'est l'écran qu'on
    /// croirait.
    ///
    /// Elles vivent en mémoire, à dessein : les perdre ne fait que retarder un
    /// envoi. L'anti-doublon, lui, ne dépend jamais d'ici — il vit dans le
    /// registre, qui survit à tout.
    /// </remarks>
    public static IServiceCollection AjouterMemoiresAgent(this IServiceCollection services)
    {
        services.AddSingleton(fournisseur => new VerificateurStabilite(
            fournisseur.GetRequiredService<IOptions<AgentOptions>>().Value.Stabilite));

        services.AddSingleton<SuiviRefus>();

        // Troisième mémoire, même raison que les deux autres : elle doit
        // survivre au tour, sans quoi le journal réécrirait tout à chaque
        // passage — ce qu'elle existe précisément pour éviter.
        services.AddSingleton<SuiviJournal>();

        return services;
    }

    /// <summary>Le tableau de bord, servi sur la boucle locale.</summary>
    public static IServiceCollection AjouterTableau(this IServiceCollection services)
    {
        // Le chemin d'envoi demandé à la main, partagé par le tableau local et
        // par les demandes venues du SaaS. Singleton, parce qu'il porte le
        // verrou anti-double-envoi : deux instances feraient deux verrous, donc
        // aucun.
        services.AddSingleton<Certification.Certificateur>();
        services.AddSingleton<Certification.ICertificateur>(
            f => f.GetRequiredService<Certification.Certificateur>());
        services.AddSingleton<Saas.TraiteurDemandes>();
        services.AddSingleton<RouteurTableau>();
        return services;
    }
}
