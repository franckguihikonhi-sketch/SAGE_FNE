using SageFne.Reader.Configuration;
using SageFne.Reader.Models.Sage;

namespace SageFne.Reader.Mapping;

/// <summary>
/// Attribue un régime d'exonération à une ligne à 0 % de TVA.
/// </summary>
/// <remarks>
/// Sage ne porte pas la différence entre TVAC et TVAD : le taux vaut 0 dans les
/// deux cas. Elle vient donc du paramétrage, de la règle la plus précise à la
/// plus générale :
///
/// <list type="number">
/// <item>l'article (AR_Ref) — quand l'exonération tient au produit ;</item>
/// <item>le client (CT_Num) — quand elle tient au titulaire d'un régime ;</item>
/// <item>le réglage global du dossier.</item>
/// </list>
///
/// Rien ne correspond : <see cref="RegimeTvaZero.Inconnu"/>, et la pièce est
/// bloquée. C'est le comportement voulu — un défaut « raisonnable » reviendrait
/// à deviner un régime fiscal.
/// </remarks>
public sealed class ZeroVatClassifier(FneOptions options)
{
    public RegimeTvaZero Classer(SageDocumentLine ligne, SageCustomer? client)
    {
        if (Lire(options.ZeroVatCategoryByArticle, ligne.ArticleReference) is { } parArticle)
        {
            return parArticle;
        }

        if (Lire(options.ZeroVatCategoryByCustomer, client?.CtNum ?? ligne.CtNum) is { } parClient)
        {
            return parClient;
        }

        return Analyser(options.ZeroVatCategory) ?? RegimeTvaZero.Inconnu;
    }

    /// <summary>
    /// Lecture tolérante du paramétrage : « TVAC » et « TVAD » sont acceptés au
    /// même titre que les noms longs, parce que c'est ce qu'on a sous les yeux
    /// dans la nomenclature FNE.
    /// </summary>
    public static RegimeTvaZero? Analyser(string? valeur) => valeur?.Trim().ToLowerInvariant() switch
    {
        "conventionalexemption" or "tvac" or "conventionnelle" => RegimeTvaZero.ExonerationConventionnelle,
        "legalexemptiontee_rme" or "legalexemptionteerme" or "tvad" or "legale"
            => RegimeTvaZero.ExonerationLegaleTeeRme,
        "unknown" or "inconnu" or "" or null => RegimeTvaZero.Inconnu,
        _ => null,
    };

    private static RegimeTvaZero? Lire(Dictionary<string, string> table, string cle)
    {
        if (string.IsNullOrWhiteSpace(cle) || !table.TryGetValue(cle.Trim(), out var valeur)) return null;

        // Une valeur illisible dans le paramétrage ne vaut pas classification :
        // mieux vaut bloquer que d'appliquer un régime mal orthographié.
        var regime = Analyser(valeur);
        return regime == RegimeTvaZero.Inconnu ? null : regime;
    }
}
