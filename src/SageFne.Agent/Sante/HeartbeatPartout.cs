using Microsoft.Extensions.Logging;

namespace SageFne.Agent.Sante;

/// <summary>
/// Publie le même battement à toutes les destinations configurées.
/// </summary>
/// <remarks>
/// Le journal du poste répond à « l'agent tourne-t-il ? » depuis la machine ;
/// la base d'audit y répond à distance, pour vingt clients à la fois. Les deux
/// servent, et aucune ne remplace l'autre — un poste dont le SaaS est éteint
/// garde son journal.
///
/// Une destination qui échoue n'empêche pas les autres. Un battement est une
/// information sur la santé de l'agent : la perdre ne doit jamais devenir un
/// problème de plus.
/// </remarks>
public sealed class HeartbeatPartout(
    IEnumerable<IPublicationHeartbeat> destinations,
    ILogger<HeartbeatPartout> logger) : IPublicationHeartbeat
{
    public async Task PublierAsync(Heartbeat battement, CancellationToken cancellation = default)
    {
        foreach (var destination in destinations)
        {
            try
            {
                await destination.PublierAsync(battement, cancellation);
            }
            catch (Exception erreur) when (erreur is not OperationCanceledException)
            {
                logger.LogWarning(
                    "Battement non publié vers {Destination} : {Pourquoi}",
                    destination.GetType().Name, erreur.Message);
            }
        }
    }
}
