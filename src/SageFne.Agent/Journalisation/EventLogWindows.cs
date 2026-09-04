using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace SageFne.Agent.Journalisation;

/// <summary>
/// Le journal d'événements Windows, pour ce qui mérite l'attention d'un
/// administrateur.
/// </summary>
/// <remarks>
/// Dans un type à part, marqué pour Windows : l'agent doit rester compilable et
/// testable sur toute plateforme, et un « if (OperatingSystem.IsWindows()) »
/// autour d'un appel n'en dit rien à l'analyseur.
///
/// Seuls les avertissements et au-delà y entrent. Un service qui inonderait
/// l'Event Log de messages de routine ferait perdre l'habitude de le lire, et
/// c'est là que se verra un jour une facture bloquée.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class EventLogWindows
{
    public static ILoggingBuilder AjouterEventLog(this ILoggingBuilder journalisation) =>
        journalisation.AddEventLog(parametres =>
        {
            parametres.SourceName = "SageFne Agent";
            parametres.Filter = (_, niveau) => niveau >= LogLevel.Warning;
        });
}
