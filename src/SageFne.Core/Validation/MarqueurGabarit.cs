namespace SageFne.Core.Validation;

/// <summary>
/// Reconnaît un texte à remplacer là où on attend une valeur.
/// </summary>
/// <remarks>
/// <b>Un seul endroit, et c'est tout l'objet de cette classe.</b> La règle
/// vivait en deux exemplaires — la liste du CLI, qui connaissait
/// <c>VOTRE_REFERENCE</c>, et celle de <see cref="FneCompleteness"/>, qui ne
/// connaissait aucun <c>VOTRE_…</c>. Elles ont divergé, et la seconde a laissé
/// passer <c>VOTRE_ETAB</c> dans l'identité du dossier auprès de la DGI : la
/// plateforme refusait toutes les factures, et rien n'avertissait plus, puisque
/// la valeur n'était pas reconnue comme un trou.
///
/// Le gabarit se reconnaît de trois façons, et il en faut trois : un mot connu
/// ne couvre que ce qu'on a déjà vu passer ; un préfixe attrape ce qu'on écrira
/// demain ; les signes typographiques attrapent ce qui vient d'un copier-coller
/// de documentation.
///
/// C'est la quatrième fois qu'un gabarit de la documentation est recopié tel
/// quel dans une commande. Le défaut n'est pas chez qui colle : c'est d'écrire
/// des exemples qui ressemblent à des valeurs.
/// </remarks>
public static class MarqueurGabarit
{
    /// <summary>Les mots déjà vus passer pour des valeurs.</summary>
    private static readonly string[] Mots =
    [
        "A_COMPLETER", "A_RENSEIGNER", "A_DEFINIR", "TODO", "XXX", "XXXX",
        "LA_REFERENCE", "TA_REFERENCE_FNE", "VOTRE_REFERENCE", "REFERENCE", "REF",
        "LE_NUMERO", "NUMERO", "MOT_DE_PASSE", "EXEMPLE", "PLACEHOLDER", "CHANGEME",
    ];

    /// <summary>
    /// Les débuts de mot qui ne peuvent désigner qu'un trou à remplir.
    /// </summary>
    /// <remarks>
    /// Aucun identifiant délivré par la DGI, aucune référence, aucun code de
    /// dossier ne commence par « VOTRE_ » ou « MON_ ». Le préfixe attrape donc
    /// ce que la liste de mots ne connaît pas encore — <c>VOTRE_POINT</c>,
    /// <c>VOTRE_ETAB</c>, et tout ce qu'un exemple futur inventera.
    /// </remarks>
    private static readonly string[] Prefixes =
    [
        "VOTRE_", "VOS_", "MON_", "MA_", "MES_", "TON_", "TES_",
        "YOUR_", "MY_", "EXEMPLE_", "SAMPLE_",
    ];

    /// <summary>Les signes d'un texte de documentation, jamais d'une valeur.</summary>
    /// <remarks>
    /// Les crochets ont rejoint les chevrons après qu'une clé de service a été
    /// posée en variable machine sous la forme que la documentation montrait.
    /// Les deux conventions se valent, et aucune valeur attendue ici — URL,
    /// jeton, UUID, point de vente, référence DGI — n'en porte.
    /// </remarks>
    private static readonly char[] Signes = ['<', '>', '[', ']', '…', '«', '»'];

    /// <summary>Vrai quand la valeur est un trou à remplir, non une valeur.</summary>
    public static bool Est(string? valeur)
    {
        if (string.IsNullOrWhiteSpace(valeur)) return false;

        var nu = valeur.Trim();

        return nu.IndexOfAny(Signes) >= 0
            || Mots.Contains(nu, StringComparer.OrdinalIgnoreCase)
            || Prefixes.Any(prefixe => nu.StartsWith(prefixe, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Vrai quand rien n'a été renseigné : vide, ou gabarit.</summary>
    public static bool Absent(string? valeur) =>
        string.IsNullOrWhiteSpace(valeur) || Est(valeur);
}
