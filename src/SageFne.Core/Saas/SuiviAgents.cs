namespace SageFne.Core.Saas;

/// <summary>Ce que l'agent sait de ses semblables sur le même dossier.</summary>
public enum ConstatAgents
{
    /// <summary>La base n'a jamais répondu : on ignore qui d'autre travaille.</summary>
    Inconnu,

    /// <summary>Aucun autre agent n'a donné signe de vie récemment.</summary>
    Seul,

    /// <summary>Au moins un autre agent bat sur ce dossier.</summary>
    Accompagne,
}

/// <summary>
/// Combien d'agents partagent ce dossier, constaté et non déclaré.
/// </summary>
/// <remarks>
/// Sert à trancher une seule question, mais elle est décisive : que faire quand
/// la base d'audit ne répond plus et qu'on ne peut donc pas réserver une pièce ?
///
/// Les deux réponses simples sont mauvaises. <b>Toujours bloquer</b> ferait
/// dépendre la certification d'un service qui n'y participe pas : un poste
/// isolé, dont le registre fichier suffit, cesserait de travailler parce qu'un
/// service distant est en panne. <b>Toujours envoyer</b> rouvrirait la porte au
/// doublon le jour où deux postes existent.
///
/// La bonne réponse dépend d'un fait — sommes-nous seuls ? — et ce fait se
/// relève au lieu de se déclarer. Un réglage que l'installateur coche est un
/// réglage qui ment le jour où l'on ajoute un second poste sans y penser.
///
/// L'ordre d'apparition rend la chose sûre : un agent qui démarre voit
/// toujours celui qui était là avant lui, alors que l'inverse prend un tour.
/// Un nouveau venu pendant une panne n'a donc jamais contacté la base — il est
/// <see cref="Inconnu"/>, et n'enverra rien. L'ancien, lui, se sait seul et
/// continue. Aucun des deux ne peut envoyer la même pièce.
///
/// Vit en mémoire, à dessein : le perdre au redémarrage ramène à
/// <see cref="Inconnu"/>, c'est-à-dire au comportement prudent.
/// </remarks>
public sealed class SuiviAgents
{
    private readonly object _verrou = new();
    private ConstatAgents _dernier = ConstatAgents.Inconnu;
    private DateTimeOffset? _vuLe;

    public ConstatAgents Dernier
    {
        get { lock (_verrou) return _dernier; }
    }

    /// <summary>Quand la base a répondu pour la dernière fois.</summary>
    public DateTimeOffset? VuLe
    {
        get { lock (_verrou) return _vuLe; }
    }

    /// <summary>Enregistre ce que la base vient de dire.</summary>
    /// <param name="autres">Agents autres que celui-ci, vus récemment.</param>
    public void Noter(int autres)
    {
        lock (_verrou)
        {
            _dernier = autres > 0 ? ConstatAgents.Accompagne : ConstatAgents.Seul;
            _vuLe = DateTimeOffset.Now;
        }
    }

    /// <summary>
    /// Vrai quand l'agent peut envoyer sans avoir pu réserver.
    /// </summary>
    /// <remarks>
    /// Uniquement s'il s'est constaté seul. Le registre fichier suffit alors :
    /// il est la mémoire complète d'un poste qui est le seul à travailler.
    /// </remarks>
    public bool PeutSePasserDeLaBase => Dernier == ConstatAgents.Seul;
}
