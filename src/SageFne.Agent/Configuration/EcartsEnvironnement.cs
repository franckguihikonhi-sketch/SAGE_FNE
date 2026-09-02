namespace SageFne.Agent.Configuration;

/// <summary>
/// Ce que la machine porte contre ce que le service applique réellement.
/// </summary>
/// <remarks>
/// Un service ne démarre pas sous le compte qui l'installe, et le gestionnaire
/// de services garde en cache l'environnement machine tel qu'il était à
/// l'amorçage de Windows. Une variable posée cinq minutes plus tôt peut donc
/// lui rester invisible : l'agent tourne parfaitement, sur d'autres réglages
/// que ceux qu'on croit avoir posés.
///
/// C'est arrivé sur ce poste avec le délai de stabilité : réglé à 2 minutes,
/// et rien ne disait si le service en appliquait 2 ou 5. On attend alors sans
/// comprendre, et l'on conclut que l'automatisme ne marche pas.
///
/// Aucune valeur de secret n'est lue ni comparée ici : pour la chaîne de
/// connexion et la clé d'API, seule leur présence est constatée, et elle ne
/// l'est que pour dire qu'elle manque.
/// </remarks>
public static class EcartsEnvironnement
{
    /// <summary>
    /// Les désaccords entre la variable machine et le réglage appliqué.
    /// </summary>
    /// <param name="applique">
    /// Ce que le service applique, par nom de variable — la valeur telle qu'elle
    /// s'écrirait dans l'environnement.
    /// </param>
    /// <param name="lire">
    /// Comment lire une variable machine. Injecté pour être éprouvable : l'API
    /// de Windows ne rend rien d'utile ailleurs.
    /// </param>
    public static IReadOnlyList<string> Detecter(
        IReadOnlyDictionary<string, string> applique,
        Func<string, string?> lire)
    {
        var ecarts = new List<string>();

        foreach (var (variable, valeurAppliquee) in applique)
        {
            var surLaMachine = lire(variable);

            // Absente de la machine : le service tourne sur appsettings.json ou
            // sur son défaut, ce qui est légitime. Rien à signaler.
            if (string.IsNullOrWhiteSpace(surLaMachine)) continue;

            if (!string.Equals(surLaMachine.Trim(), valeurAppliquee, StringComparison.OrdinalIgnoreCase))
            {
                ecarts.Add(
                    $"{variable} vaut « {surLaMachine.Trim()} » sur la machine, mais le service " +
                    $"applique « {valeurAppliquee} ». Le gestionnaire de services n'a pas vu " +
                    "cette variable : elle a été posée après l'amorçage de Windows. " +
                    "Redémarrez le poste pour qu'elle prenne effet.");
            }
        }

        return ecarts;
    }
}
