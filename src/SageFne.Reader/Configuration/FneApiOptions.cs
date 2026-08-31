namespace SageFne.Reader.Configuration;

/// <summary>
/// Accès à la plateforme FNE de la DGI.
/// </summary>
/// <remarks>
/// Tout est paramétrable, y compris le chemin et l'en-tête d'authentification :
/// la documentation de la DGI fait foi, pas ce que le code suppose. Les valeurs
/// par défaut sont celles relevées jusqu'ici, et le premier appel réel dira si
/// elles sont justes — la commande affiche la requête exacte avant de l'envoyer.
///
/// <b>La clé n'a pas sa place dans appsettings.json</b>, qui est suivi par Git.
/// Elle se pose par <c>dotnet user-secrets set "Fne:Api:ApiKey" "…"</c>.
/// </remarks>
public sealed class FneApiOptions
{
    /// <summary>Racine de l'API, sans barre finale. Vide : rien ne peut partir.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Chemin de la certification, relatif à <see cref="BaseUrl"/>.</summary>
    public string SignPath { get; set; } = "/external/invoices/sign";

    /// <summary>Clé d'API. À poser dans les secrets utilisateur, jamais ici.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>En-tête portant la clé.</summary>
    public string AuthenticationHeader { get; set; } = "Authorization";

    /// <summary>Préfixe de la valeur d'en-tête. Vide pour une clé nue.</summary>
    public string AuthenticationScheme { get; set; } = "Bearer";

    public int TimeoutSeconds { get; set; } = 30;

    public bool EstConfigure =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !BaseUrl.Contains("A_COMPLETER", StringComparison.OrdinalIgnoreCase);

    /// <summary>L'adresse complète de la certification.</summary>
    public Uri AdresseSignature() => new(new Uri(BaseUrl.TrimEnd('/') + "/"), SignPath.TrimStart('/'));

    /// <summary>
    /// La clé, réduite à ses extrémités.
    /// </summary>
    /// <remarks>
    /// La commande affiche la requête avant de l'envoyer, en-têtes compris. Une
    /// clé en clair dans une console finit copiée dans un courriel de support.
    /// Quatre caractères suffisent à vérifier qu'on utilise la bonne.
    /// </remarks>
    public string CleMasquee() => ApiKey.Length <= 8
        ? new string('•', Math.Max(ApiKey.Length, 4))
        : $"{ApiKey[..4]}{new string('•', 8)}{ApiKey[^4..]}";
}
