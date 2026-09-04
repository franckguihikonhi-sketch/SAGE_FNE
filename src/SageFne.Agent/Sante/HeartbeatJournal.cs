using Microsoft.Extensions.Logging;

namespace SageFne.Agent.Sante;

/// <summary>
/// Écrit le battement au journal. La première destination, pas la dernière.
/// </summary>
/// <remarks>
/// Suffit à répondre à « l'agent tourne-t-il ? » depuis le poste. Le jour où le
/// SaaS existera, une seconde implémentation postera le même objet vers
/// Supabase sans que l'agent change d'une ligne.
/// </remarks>
public sealed class HeartbeatJournal(ILogger<HeartbeatJournal> logger) : IPublicationHeartbeat
{
    public Task PublierAsync(Heartbeat battement, CancellationToken cancellation = default)
    {
        if (battement.EnBonneSante)
        {
            logger.LogInformation("heartbeat {Battement}", battement);
        }
        else
        {
            // Sage injoignable veut dire qu'aucune facture ne sera lue : ce
            // n'est pas une information de routine.
            logger.LogWarning("heartbeat DEGRADE {Battement}", battement);
        }

        return Task.CompletedTask;
    }
}
