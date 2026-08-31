namespace SageFne.Reader.Configuration;

/// <summary>Sur quelle plateforme le middleware travaille.</summary>
public enum FneEnvironment
{
    /// <summary>Plateforme d'essai de la DGI. Rien n'y a de valeur fiscale.</summary>
    Test,

    /// <summary>Plateforme réelle. Ce qui y est certifié engage l'entreprise.</summary>
    Production,
}

/// <summary>
/// Accès à la plateforme FNE. Lié sur la section <c>Fne</c>.
/// </summary>
/// <remarks>
/// <b>La clé n'a pas sa place dans appsettings.json</b>, qui est suivi par Git.
/// Elle se pose par <c>dotnet user-secrets set "Fne:ApiKey" "…"</c>.
///
/// Le chemin et l'en-tête d'authentification restent paramétrables : la
/// documentation de la DGI fait foi, pas ce que le code suppose.
/// </remarks>
public sealed class FneApiOptions
{
    /// <summary>
    /// Environnement visé. <see cref="FneEnvironment.Test"/> par défaut, et
    /// c'est voulu : un défaut de production ferait certifier pour de vrai une
    /// configuration oubliée.
    /// </summary>
    public FneEnvironment Environment { get; set; } = FneEnvironment.Test;

    /// <summary>Racine de l'API. Vide : rien ne peut partir.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Clé d'API. Secrets utilisateur uniquement.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Chemin de la certification, relatif à <see cref="BaseUrl"/>.</summary>
    public string SignPath { get; set; } = "/external/invoices/sign";

    public string AuthenticationHeader { get; set; } = "Authorization";

    /// <summary>Préfixe de la valeur d'en-tête. Vide pour une clé nue.</summary>
    public string AuthenticationScheme { get; set; } = "Bearer";

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Ce qui désigne une plateforme d'essai dans une adresse.
    /// </summary>
    /// <remarks>
    /// Une <b>liste d'autorisation</b>, pas une liste d'interdiction. Interdire
    /// « prod » laisserait passer n'importe quel nom d'hôte inconnu : c'est
    /// exactement ainsi qu'une adresse de production finit appelée depuis une
    /// configuration de test. Ici, ce qui n'est pas reconnu comme un
    /// environnement d'essai est refusé.
    ///
    /// Complétez la liste si la DGI nomme autrement sa plateforme d'essai.
    /// </remarks>
    /// <remarks>
    /// Vide par défaut, et non pré-remplie : le binder de configuration
    /// <b>ajoute</b> à une liste existante au lieu de la remplacer, ce qui
    /// dédoublait chaque marqueur dès que appsettings.json en portait une.
    /// Les valeurs par défaut sont donc servies par <see cref="Marqueurs"/>.
    /// </remarks>
    public List<string> TestHostMarkers { get; set; } = [];

    private static readonly string[] MarqueursParDefaut =
        ["test", "sandbox", "preprod", "recette", "uat", "demo", "localhost", "127.0.0.1"];

    /// <summary>Les marqueurs effectivement appliqués.</summary>
    public IReadOnlyList<string> Marqueurs => TestHostMarkers.Count > 0
        ? TestHostMarkers.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        : MarqueursParDefaut;

    public bool EstTest => Environment == FneEnvironment.Test;

    public bool CleRenseignee => !string.IsNullOrWhiteSpace(ApiKey);

    public bool UrlRenseignee =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && !BaseUrl.Contains("A_COMPLETER", StringComparison.OrdinalIgnoreCase);

    public bool EstConfigure => CleRenseignee && UrlRenseignee && Verifier() is null;

    /// <summary>L'adresse complète de la certification.</summary>
    public Uri AdresseSignature() => new(new Uri(BaseUrl.TrimEnd('/') + "/"), SignPath.TrimStart('/'));

    /// <summary>
    /// Ce qui empêche d'utiliser cette configuration, ou null si elle tient.
    /// </summary>
    public string? Verifier()
    {
        if (!UrlRenseignee) return "Fne:BaseUrl n'est pas renseignée.";

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var adresse))
        {
            return $"Fne:BaseUrl « {BaseUrl} » n'est pas une adresse absolue.";
        }

        if (adresse.Scheme != Uri.UriSchemeHttps && !EstLocale(adresse))
        {
            return $"Fne:BaseUrl utilise {adresse.Scheme} : la clé d'API ne doit voyager qu'en HTTPS.";
        }

        if (!EstTest) return null;

        // En TEST, l'adresse doit se reconnaître comme telle.
        var hote = adresse.Host;
        var marqueur = Marqueurs.FirstOrDefault(
            marque => hote.Contains(marque, StringComparison.OrdinalIgnoreCase));

        return marqueur is not null
            ? null
            : $"Fne:Environment vaut TEST, mais l'hôte « {hote} » ne se reconnaît pas comme une " +
              $"plateforme d'essai. Attendu l'un de : {string.Join(", ", TestHostMarkers)}. " +
              "Envoyer une facture d'essai en production la certifierait pour de vrai. " +
              "Corrigez Fne:BaseUrl, complétez Fne:TestHostMarkers, ou passez " +
              "Fne:Environment à Production en connaissance de cause.";
    }

    private static bool EstLocale(Uri adresse) =>
        adresse.IsLoopback
        || adresse.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// La clé, réduite à ses extrémités.
    /// </summary>
    /// <remarks>
    /// Jamais la valeur entière : une console finit copiée dans un courriel de
    /// support. Quatre caractères suffisent à vérifier qu'on utilise la bonne.
    /// </remarks>
    public string CleMasquee() => ApiKey.Length switch
    {
        0 => "— absente —",
        <= 8 => new string('•', 8),
        _ => $"{ApiKey[..4]}{new string('•', 8)}{ApiKey[^4..]}",
    };
}
