using System.Text.RegularExpressions;

namespace SageFne.Core.Data;

/// <summary>
/// Garde-fou sur les noms de table et de colonne.
/// </summary>
/// <remarks>
/// Les valeurs passent toujours par des paramètres, jamais par le texte de la
/// requête. Un <b>identifiant</b>, lui, ne peut pas être paramétré : il doit
/// être écrit dans le SQL. Les commandes d'exploration en désignent depuis
/// l'extérieur — le nom de la table à lire — d'où ce contrôle.
///
/// Deux verrous plutôt qu'un : la forme d'abord, puis l'existence au catalogue.
/// Un nom qui passe les deux est un identifiant réel du dossier, pas du texte.
/// </remarks>
public static partial class IdentifiantSql
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex Forme();

    public static string Verifier(string nom)
    {
        var propre = nom.Trim();

        if (!Forme().IsMatch(propre))
        {
            throw new ArgumentException(
                $"« {nom} » n'est pas un nom de table ou de colonne valide : " +
                "lettres, chiffres et soulignés seulement.",
                nameof(nom));
        }

        return propre;
    }
}
