namespace SageFne.Agent.Sante;

/// <summary>Ce qu'on sait d'une dépendance à l'instant du battement.</summary>
public enum EtatLien
{
    /// <summary>Jamais éprouvé depuis le démarrage.</summary>
    Inconnu,

    /// <summary>Répond.</summary>
    Disponible,

    /// <summary>Ne répond pas.</summary>
    Indisponible,
}

/// <summary>
/// Le battement de cœur de l'agent : de quoi savoir, à distance, qu'il tourne
/// et sur quoi il travaille.
/// </summary>
/// <remarks>
/// Un service Windows sans interface est muet par construction. Sans ce
/// battement, la seule façon de savoir qu'il est mort serait de constater que
/// des factures ne partent plus — c'est-à-dire trop tard.
///
/// <see cref="EtatLien.Inconnu"/> occupe la place zéro pour la même raison que
/// dans le registre : un champ absent ne doit pas se relire comme « tout va
/// bien ».
/// </remarks>
/// <param name="AgentId">Qui bat. Deux postes ne se confondent pas.</param>
/// <param name="Environnement">TEST ou PRODUCTION, tel que l'agent le voit.</param>
public sealed record Heartbeat(
    string AgentId,
    string CompanyId,
    string Version,
    DateTimeOffset Quand,
    EtatLien Sage,
    EtatLien Reseau,
    string Environnement,
    string Mode)
{
    /// <summary>Depuis quand l'agent n'a rien fait d'utile.</summary>
    public DateTimeOffset? DerniereActivite { get; init; }

    /// <summary>Pièces examinées depuis le démarrage.</summary>
    public long PiecesExaminees { get; init; }

    /// <summary>Pièces effectivement envoyées depuis le démarrage.</summary>
    public long PiecesEnvoyees { get; init; }

    /// <summary>Pièces en attente d'une décision humaine.</summary>
    public int EnAttente { get; init; }

    /// <summary>Vrai quand tout ce dont l'agent dépend répond.</summary>
    public bool EnBonneSante => Sage == EtatLien.Disponible;

    /// <summary>
    /// Ce qui n'est pas renseigné se dit, au lieu de laisser un champ vide.
    /// </summary>
    /// <remarks>
    /// « dossier= » suivi de rien se lit comme un dossier nommé par la chaîne
    /// vide, et deux agents non paramétrés s'y confondraient dans un tableau de
    /// bord. Le champ absent doit se voir comme absent.
    /// </remarks>
    private static string Renseigne(string valeur) =>
        string.IsNullOrWhiteSpace(valeur) ? "(non renseigné)" : valeur;

    /// <summary>Une ligne de journal, sans rien d'exploitable pour un tiers.</summary>
    /// <remarks>
    /// Ni clé, ni adresse, ni nom de client : ce battement finira dans un
    /// fichier, un Event Log, puis une télémétrie SaaS. Ce qui n'y entre pas
    /// n'en fuitera pas.
    /// </remarks>
    public override string ToString() =>
        $"agent={AgentId} dossier={Renseigne(CompanyId)} v={Version} mode={Mode} env={Environnement} " +
        $"sage={Sage} reseau={Reseau} examinees={PiecesExaminees} envoyees={PiecesEnvoyees} " +
        $"attente={EnAttente}";
}

/// <summary>
/// Où part le battement.
/// </summary>
/// <remarks>
/// Une interface parce que la destination changera : fichier aujourd'hui,
/// Event Log ensuite, table Supabase quand le SaaS existera. L'agent ne doit
/// pas être réécrit à chaque fois.
/// </remarks>
public interface IPublicationHeartbeat
{
    Task PublierAsync(Heartbeat battement, CancellationToken cancellation = default);
}
