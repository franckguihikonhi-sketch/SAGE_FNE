using System.Text.RegularExpressions;

namespace SageFne.Core.Data;

/// <summary>
/// Filet de sécurité : rien d'autre qu'un SELECT ne part vers la base Sage.
/// </summary>
/// <remarks>
/// La consigne du projet est un accès strictement en lecture. Une relecture
/// attentive du code suffirait à s'en assurer aujourd'hui ; ce contrôle
/// s'assure qu'une modification distraite de demain échoue avant d'atteindre
/// le serveur, plutôt qu'après.
/// </remarks>
public static partial class ReadOnlyGuard
{
    [GeneratedRegex(@"\b(insert|update|delete|merge|alter|drop|create|truncate|exec|execute|grant|revoke)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Ecriture();

    public static string Verify(string sql)
    {
        var propre = sql.Trim();

        if (!propre.StartsWith("select", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Requête refusée : seules les lectures sont autorisées sur la base Sage.{Environment.NewLine}{propre}");
        }

        var interdit = Ecriture().Match(propre);
        if (interdit.Success)
        {
            throw new InvalidOperationException(
                $"Requête refusée : le mot-clé « {interdit.Value} » modifierait la base Sage.");
        }

        if (propre.Contains(';'))
        {
            throw new InvalidOperationException(
                "Requête refusée : une seule instruction par commande, sans point-virgule.");
        }

        return propre;
    }
}
