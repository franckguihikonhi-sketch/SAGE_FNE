using System.Collections.Concurrent;

namespace SageFne.Agent.Surveillance;

/// <summary>
/// Ce qu'on sait d'une pièce vue au moins une fois.
/// </summary>
/// <param name="Empreinte">Le contenu tel qu'il était à la dernière lecture.</param>
/// <param name="VueLe">Quand cette empreinte-là a été constatée.</param>
public readonly record struct Observation(string Empreinte, DateTimeOffset VueLe);

/// <summary>
/// Deux lectures identiques, séparées par un délai : la pièce ne bouge plus.
/// </summary>
/// <remarks>
/// Une facture apparaît dans Sage dès la première ligne saisie. La certifier à
/// cet instant certifierait un brouillon — et une facture certifiée ne s'annule
/// pas, elle se corrige par un avoir. L'agent attend donc de voir deux fois le
/// même contenu.
///
/// Ce suivi vit en mémoire, à dessein. Le perdre au redémarrage ne fait que
/// retarder un envoi : la pièce sera revue, réobservée, et repartira au tour
/// suivant. L'anti-doublon, lui, ne dépend jamais d'ici — il vit dans le
/// registre des certifications, qui survit à tout. Confondre les deux ferait
/// d'une mémoire volatile la garantie contre le doublon, ce qu'elle ne peut
/// pas être.
/// </remarks>
public sealed class VerificateurStabilite(TimeSpan delai, TimeProvider? horloge = null)
{
    private readonly TimeProvider _horloge = horloge ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Observation> _vues = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Combien de pièces sont en cours d'observation.</summary>
    public int EnObservation => _vues.Count;

    /// <summary>
    /// Constate l'état d'une pièce et dit si elle peut être considérée stable.
    /// </summary>
    /// <param name="identite">domaine / DO_DocType / DO_Piece.</param>
    /// <param name="empreinte">Empreinte du contenu traduit.</param>
    public MotifAttente Constater(string identite, string empreinte)
    {
        // Une pièce qui ne se traduit pas n'a pas d'empreinte : elle ne peut
        // être ni stable ni instable, seulement bloquée. Le dire ici évite de
        // traiter deux absences comme une égalité.
        if (string.IsNullOrEmpty(empreinte)) return MotifAttente.NonConforme;

        var maintenant = _horloge.GetUtcNow();

        if (!_vues.TryGetValue(identite, out var precedente))
        {
            _vues[identite] = new Observation(empreinte, maintenant);
            return MotifAttente.JamaisVue;
        }

        // Le contenu a bougé : la saisie continue. L'horloge repart de cette
        // lecture-ci, sans quoi une facture modifiée juste avant l'échéance
        // partirait sur la foi d'un délai écoulé pour une version précédente.
        if (!string.Equals(precedente.Empreinte, empreinte, StringComparison.Ordinal))
        {
            _vues[identite] = new Observation(empreinte, maintenant);
            return MotifAttente.ContenuInstable;
        }

        if (maintenant - precedente.VueLe < delai) return MotifAttente.DelaiNonEcoule;

        return MotifAttente.Aucun;
    }

    /// <summary>
    /// Oublie une pièce dont le sort est réglé.
    /// </summary>
    /// <remarks>
    /// Sans cela, le suivi grossirait indéfiniment sur un dossier qui facture
    /// tous les jours. L'oubli ne fait rien perdre : le registre garde ce qui
    /// compte.
    /// </remarks>
    public void Oublier(string identite) => _vues.TryRemove(identite, out _);

    /// <summary>Ce qui est retenu d'une pièce, pour le diagnostic.</summary>
    public Observation? Derniere(string identite) =>
        _vues.TryGetValue(identite, out var vue) ? vue : null;
}
