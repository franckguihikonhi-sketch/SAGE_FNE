namespace SageFne.Core.Configuration;

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
    /// Les adresses d'essai autorisées, exactement.
    /// </summary>
    /// <remarks>
    /// La procédure publiée par la DGI donne <c>http://54.247.95.108/ws</c> pour
    /// l'environnement d'essai : du HTTP clair, sur une adresse IP nue. Ce n'est
    /// pas défendable en général — la clé d'API y voyage en clair, lisible de
    /// tout équipement traversé.
    ///
    /// D'où une <b>exception nominative plutôt qu'une règle</b> : HTTP n'est
    /// jamais autorisé en tant que tel, cette adresse-ci l'est. Toute autre
    /// adresse, en HTTP comme en HTTPS, est refusée en environnement d'essai.
    ///
    /// La liste reste modifiable — pour un bouchon local, par exemple — mais
    /// l'ajout est alors un acte délibéré, pas un effet de bord.
    /// </remarks>
    public List<string> TestAllowedUrls { get; set; } = [];

    private static readonly string[] AdressesEssaiParDefaut = ["http://54.247.95.108/ws"];

    /// <summary>Les adresses d'essai effectivement admises, normalisées.</summary>
    public IReadOnlyList<string> AdressesAutorisees =>
        (TestAllowedUrls.Count > 0 ? TestAllowedUrls.AsEnumerable() : AdressesEssaiParDefaut)
        .Select(Normaliser)
        .Where(adresse => adresse is not null)
        .Select(adresse => adresse!)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    public bool EstTest => Environment == FneEnvironment.Test;

    public bool CleRenseignee => !string.IsNullOrWhiteSpace(ApiKey);

    public bool UrlRenseignee =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && !BaseUrl.Contains("A_COMPLETER", StringComparison.OrdinalIgnoreCase);

    public bool EstConfigure => CleRenseignee && UrlRenseignee && Verifier() is null;

    /// <summary>
    /// L'adresse retenue est en HTTP clair.
    /// </summary>
    /// <remarks>
    /// Vrai pour l'adresse d'essai de la DGI. La clé y voyage en clair : n'y
    /// utilisez jamais une clé de production, et considérez-la comme exposée.
    /// </remarks>
    public bool EnClair => BaseUrlEffective.StartsWith("http://", StringComparison.Ordinal);

    /// <summary>L'adresse complète de la certification.</summary>
    public Uri AdresseSignature() =>
        new(new Uri(BaseUrlEffective.TrimEnd('/') + "/"), SignPath.TrimStart('/'));

    /// <summary>
    /// L'adresse retenue après normalisation, ou celle configurée telle quelle.
    /// </summary>
    /// <remarks>
    /// <c>http://54.247.95.108</c> sans le <c>/ws</c> désigne visiblement la même
    /// plateforme, mais y ajouter le chemin de signature donnerait
    /// <c>/external/invoices/sign</c> au lieu de <c>/ws/external/invoices/sign</c>
    /// — une adresse fausse, et un échec incompréhensible. L'adresse est donc
    /// ramenée à celle de la liste dont elle ne diffère que par le chemin.
    /// </remarks>
    public string BaseUrlEffective
    {
        get
        {
            var normalisee = Normaliser(BaseUrl);
            if (normalisee is null) return BaseUrl;
            if (AdressesAutorisees.Contains(normalisee, StringComparer.Ordinal)) return normalisee;

            var completions = AdressesAutorisees.Where(autorisee =>
                autorisee.StartsWith(normalisee + "/", StringComparison.Ordinal)).ToList();

            // Une seule complétion possible : c'est un raccourci, pas une
            // ambiguïté. Plusieurs : on ne devine pas.
            return completions.Count == 1 ? completions[0] : normalisee;
        }
    }

    /// <summary>
    /// Ce qui empêche d'utiliser cette configuration, ou null si elle tient.
    /// </summary>
    public string? Verifier()
    {
        if (!UrlRenseignee) return "Fne:BaseUrl n'est pas renseignée.";

        if (Normaliser(BaseUrl) is null)
        {
            return $"Fne:BaseUrl « {BaseUrl} » n'est pas une adresse http(s) absolue.";
        }

        var effective = BaseUrlEffective;

        if (EstTest)
        {
            // Liste d'autorisation exacte. Rien d'autre ne passe, quel que soit
            // le protocole : ni une autre adresse HTTP, ni une HTTPS inconnue.
            return AdressesAutorisees.Contains(effective, StringComparer.Ordinal)
                ? null
                : $"Fne:Environment vaut TEST : seules les adresses d'essai déclarées sont admises, " +
                  $"et « {BaseUrl} » n'en fait pas partie. Autorisée(s) : " +
                  $"{string.Join(", ", AdressesAutorisees)}. " +
                  "Envoyer une facture d'essai ailleurs pourrait la certifier pour de vrai. " +
                  "Corrigez Fne:BaseUrl, ou déclarez l'adresse dans Fne:TestAllowedUrls " +
                  "en sachant ce que vous faites.";
        }

        // En production, aucune exception : la clé ne voyage pas en clair.
        return effective.StartsWith("https://", StringComparison.Ordinal)
            ? null
            : "Fne:Environment vaut Production et Fne:BaseUrl n'est pas en HTTPS. " +
              "La clé d'API voyagerait en clair : refusé sans exception.";
    }

    /// <summary>
    /// Ramène une adresse à une forme comparable : protocole et hôte en
    /// minuscules, port implicite retiré, barres finales supprimées.
    /// </summary>
    /// <returns><c>null</c> si ce n'est pas une adresse http(s) absolue.</returns>
    public static string? Normaliser(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var adresse)) return null;
        if (adresse.Scheme != Uri.UriSchemeHttp && adresse.Scheme != Uri.UriSchemeHttps) return null;

        var port = adresse.IsDefaultPort ? "" : $":{adresse.Port}";
        var chemin = adresse.AbsolutePath.TrimEnd('/');

        return $"{adresse.Scheme.ToLowerInvariant()}://{adresse.Host.ToLowerInvariant()}{port}{chemin}";
    }

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
