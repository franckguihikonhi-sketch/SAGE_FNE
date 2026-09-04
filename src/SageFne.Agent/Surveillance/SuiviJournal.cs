namespace SageFne.Agent.Surveillance;

/// <summary>
/// Ne réécrit au journal que ce qui a changé.
/// </summary>
/// <remarks>
/// Le tour de garde écrivait une ligne par pièce retenue, à chaque passage.
/// Sur un dossier de quatorze pièces déjà traitées, cela faisait quatorze
/// lignes par minute — vingt mille par jour — pour des pièces qui ne bougeront
/// plus jamais. La proportion empire à mesure que les factures s'accumulent
/// dans la fenêtre.
///
/// <b>Un journal qu'on ne peut plus lire est un journal qui ne sert plus</b>, et
/// c'est là qu'on cherche quand quelque chose ne va pas.
///
/// Ce qui est retenu n'est pas perdu : le tour écrit à la place une ligne de
/// synthèse, qui dit combien de pièces sont dans quel état. Le détail
/// réapparaît dès qu'une pièce change d'état — c'est-à-dire dès qu'il apprend
/// quelque chose.
///
/// La mémoire vit en mémoire vive, à dessein : la perdre ne fait que réécrire
/// une fois le détail de chaque pièce au redémarrage. C'est même souhaitable —
/// le journal d'un service qui vient de démarrer doit dire où il en est.
/// </remarks>
public sealed class SuiviJournal
{
    private readonly Dictionary<string, string> _dernier = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Combien de pièces sont suivies.</summary>
    public int Suivies => _dernier.Count;

    /// <summary>
    /// Vrai quand cette décision mérite une ligne : elle est nouvelle, ou elle
    /// a changé depuis la dernière fois.
    /// </summary>
    public bool AEcrire(DecisionAgent decision)
    {
        // L'explication entière, et non le seul motif : le compte à rebours de
        // stabilité y figure, et un « il reste 240 s » suivi d'un « il reste
        // 180 s » sont deux informations différentes. Le motif seul les aurait
        // confondus et l'exploitant n'aurait plus vu la pièce avancer.
        var etat = $"{decision.Motif}|{decision.Explication}";

        if (_dernier.TryGetValue(decision.Identite, out var precedent)
            && string.Equals(precedent, etat, StringComparison.Ordinal))
        {
            return false;
        }

        _dernier[decision.Identite] = etat;
        return true;
    }

    /// <summary>Oublie une pièce : sa prochaine décision sera réécrite.</summary>
    public void Oublier(string identite) => _dernier.Remove(identite);

    /// <summary>
    /// La synthèse d'un tour, en une ligne — ce que le détail retenu aurait dit.
    /// </summary>
    public static string Synthese(IReadOnlyList<DecisionAgent> decisions)
    {
        if (decisions.Count == 0) return "Aucune pièce sur la fenêtre.";

        var parts = decisions
            .GroupBy(decision => decision.Motif)
            .OrderByDescending(groupe => groupe.Count())
            .Select(groupe => $"{groupe.Count()} {Nommer(groupe.Key)}");

        return $"{decisions.Count} pièce(s) : {string.Join(", ", parts)}.";
    }

    private static string Nommer(MotifAttente motif) => motif switch
    {
        MotifAttente.Aucun => "prête à partir",
        MotifAttente.JamaisVue => "vue pour la première fois",
        MotifAttente.DelaiNonEcoule => "en attente de stabilité",
        MotifAttente.ContenuInstable => "en cours de saisie",
        MotifAttente.NonConforme => "bloquée par un contrôle",
        MotifAttente.DejaTraitee => "déjà traitée",
        MotifAttente.RefusInchange => "refusée, en attente d'un nouvel essai",
        MotifAttente.HorsPerimetre => "hors périmètre",
        MotifAttente.ModeNonAutomatique => "prête, en attente d'une décision",
        MotifAttente.PlateformeInjoignable => "en file, plateforme injoignable",
        _ => motif.ToString(),
    };
}
