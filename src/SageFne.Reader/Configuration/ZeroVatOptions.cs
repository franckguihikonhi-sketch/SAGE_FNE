namespace SageFne.Reader.Configuration;

/// <summary>
/// Classification des lignes à 0 % de TVA, du plus précis au plus général.
/// </summary>
/// <remarks>
/// Sage ne distingue pas l'exonération conventionnelle de l'exonération légale :
/// le taux vaut 0 dans les deux cas, et la vérification faite sur le dossier HT
/// l'a confirmé — <c>F_TAXE</c> n'a aucune fiche à taux 0, et une ligne exonérée
/// ne porte aucun code de taxe. La distinction est un fait juridique que Sage
/// n'a jamais eu de raison d'enregistrer.
///
/// Elle se déclare donc ici. La structure est volontairement plate — quatre
/// dictionnaires de chaînes — pour qu'une interface de paramétrage puisse les
/// alimenter plus tard sans que le code change : c'est
/// <see cref="Mapping.IZeroVatPolicy"/> qui compte, pas d'où viennent les règles.
/// </remarks>
public sealed class ZeroVatOptions
{
    /// <summary>Règle du dossier, appliquée à défaut de plus précis.</summary>
    public string Default { get; set; } = "Unknown";

    /// <summary>Par référence d'article (AR_Ref). Priorité 1.</summary>
    public Dictionary<string, string> ByArticle { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Par famille d'article (FA_CodeFamille). Priorité 2.</summary>
    public Dictionary<string, string> ByFamily { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Par compte client (CT_Num). Priorité 3.</summary>
    public Dictionary<string, string> ByCustomer { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
