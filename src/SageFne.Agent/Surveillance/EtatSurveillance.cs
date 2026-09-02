using SageFne.Core.Fne;

namespace SageFne.Agent.Surveillance;

/// <summary>
/// Comment l'agent se comporte face à une facture prête.
/// </summary>
/// <remarks>
/// Le mode ne change jamais les règles métier : une pièce non conforme est
/// bloquée dans les trois. Il ne décide que d'une chose — qui appuie sur le
/// bouton.
/// </remarks>
public enum ModeAgent
{
    /// <summary>
    /// L'agent observe et signale. Il n'envoie rien, jamais.
    /// </summary>
    /// <remarks>
    /// Le mode par défaut, et le seul qui ne puisse rien certifier par
    /// surprise. Une facture certifiée ne s'annule pas : le mode qui envoie
    /// doit se choisir, pas s'hériter.
    /// </remarks>
    Manual,

    /// <summary>
    /// L'agent prépare tout et s'arrête au bord : la pièce est marquée prête,
    /// un humain déclenche l'envoi.
    /// </summary>
    SemiAutomatic,

    /// <summary>
    /// L'agent envoie les pièces conformes et stables, sans intervention.
    /// </summary>
    Automatic,
}

/// <summary>
/// Pourquoi une pièce n'est pas partie à ce passage.
/// </summary>
public enum MotifAttente
{
    /// <summary>Rien ne s'y oppose.</summary>
    Aucun,

    /// <summary>Vue pour la première fois : son contenu n'est pas encore stable.</summary>
    JamaisVue,

    /// <summary>Revue trop tôt : le délai de stabilité n'est pas écoulé.</summary>
    DelaiNonEcoule,

    /// <summary>Le contenu a changé entre deux lectures : la saisie continue.</summary>
    ContenuInstable,

    /// <summary>Des contrôles métier la bloquent.</summary>
    NonConforme,

    /// <summary>Déjà certifiée, déjà partie, ou déposée au portail.</summary>
    DejaTraitee,

    /// <summary>Le mode courant n'autorise pas l'envoi automatique.</summary>
    ModeNonAutomatique,

    /// <summary>
    /// Rien n'est parti : la plateforme est injoignable, ou le réseau est
    /// coupé. La pièce reste en file et sera retentée.
    /// </summary>
    /// <remarks>
    /// À distinguer absolument d'une panne survenue <b>après</b> le départ du
    /// POST : celle-là laisse la pièce en <see cref="EtatFne.Sending"/> et
    /// interdit tout renvoi automatique.
    /// </remarks>
    PlateformeInjoignable,
}

/// <summary>
/// Ce que l'agent a décidé pour une pièce, et pourquoi.
/// </summary>
/// <param name="Piece">Le numéro de pièce Sage.</param>
/// <param name="Identite">domaine / DO_DocType / DO_Piece.</param>
/// <param name="Motif">Ce qui a retenu l'envoi, ou <see cref="MotifAttente.Aucun"/>.</param>
/// <param name="Explication">Le même motif, en clair, pour le journal.</param>
public sealed record DecisionAgent(
    string Piece,
    string Identite,
    MotifAttente Motif,
    string Explication)
{
    public bool Envoyable => Motif == MotifAttente.Aucun;
}
