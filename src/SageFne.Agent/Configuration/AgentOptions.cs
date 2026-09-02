using SageFne.Agent.Surveillance;

namespace SageFne.Agent.Configuration;

/// <summary>
/// Le paramétrage de l'agent, section <c>Agent</c> d'appsettings.json.
/// </summary>
public sealed class AgentOptions
{
    public const string Section = "Agent";

    /// <summary>
    /// Ce que l'agent a le droit de faire.
    /// </summary>
    /// <remarks>
    /// <see cref="ModeAgent.Manual"/> par défaut, et ce n'est pas de la
    /// prudence décorative : une facture certifiée ne s'annule pas. Un
    /// paramétrage incomplet, un fichier oublié, une valeur mal orthographiée —
    /// tout cela doit retomber sur le mode qui n'envoie rien, jamais sur celui
    /// qui envoie tout.
    /// </remarks>
    public ModeAgent Mode { get; set; } = ModeAgent.Manual;

    /// <summary>Combien de temps entre deux tours de surveillance.</summary>
    public int IntervalleSecondes { get; set; } = 60;

    /// <summary>
    /// Délai entre les deux lectures qui établissent qu'une pièce ne bouge plus.
    /// </summary>
    /// <remarks>
    /// Trop court, on certifie des saisies en cours ; trop long, la facture
    /// part le lendemain. Cinq minutes est un point de départ, pas une vérité :
    /// il se règle sur la manière dont le dossier saisit.
    /// </remarks>
    public int StabiliteMinutes { get; set; } = 5;

    /// <summary>
    /// Sur combien de jours en arrière l'agent regarde à chaque tour.
    /// </summary>
    /// <remarks>
    /// Une fenêtre, pas tout le dossier : relire mille factures chaque minute
    /// n'apprend rien de plus et pèse sur le serveur Sage. Assez large,
    /// toutefois, pour rattraper un agent arrêté pendant un week-end.
    /// </remarks>
    public int FenetreJours { get; set; } = 7;

    /// <summary>Plafond de pièces examinées par tour.</summary>
    public int LimiteParTour { get; set; } = 200;

    /// <summary>Plafond de pièces réellement <b>envoyées</b> par tour.</summary>
    /// <remarks>
    /// Lire deux cents pièces est sans conséquence ; en certifier deux cents en
    /// une minute ne se défait pas. Ces deux plafonds n'ont donc rien à voir, et
    /// confondre le second avec le premier reviendrait à n'en avoir aucun.
    ///
    /// Le risque n'est pas théorique. Le dossier porte mille quatre pièces dont
    /// la plupart ne sont bloquées que par un NCC ou un téléphone absent. Le
    /// jour où ces fiches clients sont complétées dans Sage — ce qui est
    /// précisément le travail en cours — un lot entier devient conforme d'un
    /// coup. Sans plafond, le premier tour qui suit part avec.
    ///
    /// Dix par tour, une minute entre les tours : de quoi voir au journal ce qui
    /// se passe et arrêter le service avant que cela ne devienne irréversible.
    /// Ce qui dépasse n'est pas perdu, seulement remis au tour suivant.
    /// </remarks>
    public int LimiteEnvoisParTour { get; set; } = 10;

    /// <summary>Où l'agent écrit son journal.</summary>
    /// <remarks>
    /// Vide : le dossier de données de l'application. Jamais à côté du binaire —
    /// un registre y a déjà été perdu par un <c>dotnet clean</c>.
    /// </remarks>
    public string CheminJournal { get; set; } = "";

    /// <summary>Combien de jours de journal sont conservés.</summary>
    public int RetentionJournalJours { get; set; } = 30;

    /// <summary>
    /// Identifiant de cet agent, pour le heartbeat et la future télémétrie.
    /// </summary>
    /// <remarks>
    /// Vide : dérivé du nom de la machine. Deux agents sur deux postes ne
    /// doivent jamais se confondre dans un tableau de bord.
    /// </remarks>
    public string AgentId { get; set; } = "";

    /// <summary>Le dossier surveillé, tel que le SaaS le nommera.</summary>
    public string CompanyId { get; set; } = "";

    /// <summary>Intervalle du battement de cœur, en secondes.</summary>
    public int HeartbeatSecondes { get; set; } = 300;

    public TimeSpan Intervalle => TimeSpan.FromSeconds(Math.Max(5, IntervalleSecondes));
    public TimeSpan Stabilite => TimeSpan.FromMinutes(Math.Max(0, StabiliteMinutes));
    public TimeSpan Heartbeat => TimeSpan.FromSeconds(Math.Max(30, HeartbeatSecondes));
}
