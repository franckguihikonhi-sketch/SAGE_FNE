using System.Collections.Concurrent;

namespace SageFne.Agent.Surveillance;

/// <summary>Ce qu'on sait des refus essuyés sur un contenu donné.</summary>
/// <param name="Empreinte">Le corps qui a été refusé.</param>
/// <param name="Tentatives">Combien de fois ce corps exact a été refusé.</param>
/// <param name="DernierLe">Quand le dernier refus a été constaté.</param>
public readonly record struct Refus(string Empreinte, int Tentatives, DateTimeOffset DernierLe);

/// <summary>
/// Quand réessayer une pièce que la plateforme a refusée.
/// </summary>
/// <remarks>
/// Deux fautes se font face, et il a fallu les deux pour trouver le milieu.
///
/// <b>Réessayer à chaque tour.</b> Sur le premier poste en Automatic, une pièce
/// refusée en 400 est repartie une fois par minute, indéfiniment, martelant la
/// plateforme sans rien apprendre.
///
/// <b>Ne jamais réessayer un contenu inchangé.</b> Ma première correction, et
/// elle reposait sur une supposition : qu'un 400 soit déterministe. La pièce
/// 1225 l'a démentie — refusée cinq fois entre 13:28 et 13:32, elle est passée
/// en 201 à 13:42, corps identique. Le refus était passager. Sans réessai,
/// elle serait restée bloquée jusqu'à ce qu'un humain s'en aperçoive.
///
/// D'où l'attente qui grandit : 5 minutes, puis 15, puis 45, puis 2 heures. Un
/// refus passager se rattrape tout seul en moins d'une demi-heure ; un refus
/// réel cesse d'être retenté après cinq essais, et le journal le dit.
///
/// Le suivi vit en mémoire, comme celui de la stabilité et pour la même raison :
/// le perdre au redémarrage ne fait que réessayer plus tôt. L'anti-doublon ne
/// dépend jamais d'ici — il vit au registre.
/// </remarks>
public sealed class SuiviRefus(TimeProvider? horloge = null)
{
    private readonly TimeProvider _horloge = horloge ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Refus> _refus = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Au-delà, on cesse de réessayer et on le dit.</summary>
    public const int TentativesMaximum = 5;

    /// <summary>Les attentes successives, du premier refus au dernier.</summary>
    private static readonly TimeSpan[] Attentes =
    [
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(45),
        TimeSpan.FromHours(2),
    ];

    /// <summary>Combien de pièces sont en attente d'un nouvel essai.</summary>
    public int EnAttente => _refus.Count;

    /// <summary>
    /// Constate un refus et dit quand la pièce pourra repartir.
    /// </summary>
    /// <remarks>
    /// À appeler une fois par tour tant que le registre porte le refus. Le
    /// compteur n'avance qu'au premier constat de chaque envoi : sans cela, un
    /// tour de lecture toutes les minutes épuiserait les cinq tentatives en cinq
    /// minutes sans qu'aucun envoi n'ait eu lieu.
    /// </remarks>
    /// <param name="identite">domaine / DO_DocType / DO_Piece.</param>
    /// <param name="empreinte">Empreinte du corps refusé.</param>
    /// <param name="refuseLe">Quand la plateforme a refusé, d'après le registre.</param>
    public DecisionRefus Constater(string identite, string empreinte, DateTimeOffset refuseLe)
    {
        var maintenant = _horloge.GetUtcNow();

        var suivi = _refus.AddOrUpdate(
            identite,
            _ => new Refus(empreinte, 1, refuseLe),
            (_, precedent) => precedent.Empreinte == empreinte
                // Le refus n'avance que si la plateforme en a constaté un
                // nouveau : c'est l'horodatage du registre qui le prouve, pas le
                // passage de l'agent.
                ? precedent with
                {
                    Tentatives = refuseLe > precedent.DernierLe
                        ? precedent.Tentatives + 1
                        : precedent.Tentatives,
                    DernierLe = refuseLe > precedent.DernierLe ? refuseLe : precedent.DernierLe,
                }
                // Contenu différent : la pièce a été corrigée, l'histoire des
                // refus précédents ne la concerne plus.
                : new Refus(empreinte, 1, refuseLe));

        if (suivi.Tentatives >= TentativesMaximum)
        {
            return new DecisionRefus(false, suivi.Tentatives, null);
        }

        var attente = Attentes[Math.Min(suivi.Tentatives - 1, Attentes.Length - 1)];
        var reste = suivi.DernierLe + attente - maintenant;

        return reste <= TimeSpan.Zero
            ? new DecisionRefus(true, suivi.Tentatives, TimeSpan.Zero)
            : new DecisionRefus(false, suivi.Tentatives, reste);
    }

    /// <summary>Oublie une pièce dont le sort est réglé.</summary>
    public void Oublier(string identite) => _refus.TryRemove(identite, out _);
}

/// <summary>Ce que le suivi conclut d'un refus.</summary>
/// <param name="PeutRepartir">Vrai si l'attente est écoulée.</param>
/// <param name="Tentatives">Combien de refus ont été essuyés sur ce corps.</param>
/// <param name="Reste">
/// Ce qu'il reste à attendre, ou null quand on a cessé de réessayer.
/// </param>
public readonly record struct DecisionRefus(bool PeutRepartir, int Tentatives, TimeSpan? Reste);
