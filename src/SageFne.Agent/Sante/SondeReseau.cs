using System.Net.Sockets;

namespace SageFne.Agent.Sante;

/// <summary>
/// Dit si la plateforme est joignable, sans rien lui envoyer.
/// </summary>
/// <remarks>
/// C'est la pièce qui rend la règle 15 tenable. Une fois le POST parti, on ne
/// peut plus savoir si la coupure a eu lieu avant ou après que la DGI ait reçu
/// la requête — et dans le doute, la pièce reste en <c>Sending</c>, bloquée
/// jusqu'à vérification humaine au portail.
///
/// Plutôt que de trancher ce doute après coup, on l'évite : si la plateforme ne
/// répond pas à une simple ouverture de connexion, l'agent n'entre pas dans le
/// chemin d'envoi. Rien n'est marqué, rien n'est parti, la pièce reste en file
/// et repartira au tour suivant.
///
/// La sonde n'ouvre qu'un socket. Elle n'envoie aucune requête HTTP, ne porte
/// aucune clé, et ne peut donc rien certifier par accident.
/// </remarks>
public interface ISondeReseau
{
    Task<bool> JoignableAsync(CancellationToken cancellation = default);

    /// <summary>Le même essai, mais qui dit ce qu'il a éprouvé et ce qu'il a obtenu.</summary>
    Task<ResultatSonde> EprouverAsync(CancellationToken cancellation = default);
}

/// <summary>Ce qu'un essai de joignabilité a réellement établi.</summary>
/// <remarks>
/// « INJOIGNABLE » tout court a été lu, sur le premier poste, comme « la
/// plateforme de la DGI est en panne ». Ce n'est pas ce que la sonde sait : elle
/// sait qu'une connexion TCP vers un hôte et un port n'a pas abouti. Un
/// pare-feu, un proxy d'entreprise ou une règle sortante produisent le même
/// refus alors que la plateforme répond parfaitement par ailleurs — le CLI, lui,
/// passe par HttpClient, qui suit le proxy du système.
///
/// La distinction n'est pas académique : elle décide si l'on appelle la DGI ou
/// l'administrateur réseau.
/// </remarks>
/// <param name="Joignable">Vrai si la connexion s'est ouverte.</param>
/// <param name="Cible">Ce qui a été éprouvé, hôte et port.</param>
/// <param name="Detail">Pourquoi, dans les termes du système.</param>
public readonly record struct ResultatSonde(bool Joignable, string Cible, string Detail)
{
    /// <summary>Une phrase pour le journal, qui ne dit que ce qui a été établi.</summary>
    public string Explication => Joignable
        ? $"connexion ouverte vers {Cible}"
        : $"connexion TCP vers {Cible} refusée ({Detail}). Cela ne prouve pas que la " +
          "plateforme est en panne : un pare-feu ou un proxy sortant donne le même refus.";
}

/// <summary>Choisit la sonde qui convient à une adresse.</summary>
public static class SondeReseau
{
    /// <summary>
    /// Une sonde TCP si l'adresse est exploitable, une sonde qui dit non sinon.
    /// </summary>
    /// <remarks>
    /// Hors du Program pour être éprouvable : la faute qu'elle porte - lire une
    /// adresse vide et conclure « injoignable » - vivait dans une lambda de
    /// câblage, où aucun test ne pouvait l'atteindre.
    ///
    /// Sans adresse, rien n'est joignable, et c'est la bonne réponse : une sonde
    /// qui répondrait « oui » par défaut ferait entrer l'agent dans le chemin
    /// d'envoi avec une configuration vide.
    /// </remarks>
    public static ISondeReseau Pour(string? adresse, TimeSpan delai) =>
        Uri.TryCreate(adresse, UriKind.Absolute, out var uri) && DialablePar(uri)
            ? new SondeTcp(uri, delai)
            : new SondeFigee(false);

    /// <summary>Un schéma vers lequel ouvrir un socket a un sens.</summary>
    /// <remarks>
    /// Sous Unix, <c>Uri.TryCreate</c> accepte « /external/invoices/sign » comme
    /// adresse absolue et en fait un <c>file://</c> — sous Windows, non. Une
    /// BaseUrl mal saisie donnerait donc une sonde TCP vers un hôte vide sur un
    /// poste et une sonde figée sur l'autre, avec un journal qui parlerait de
    /// « :80 ». Le même piège que Path.IsPathRooted, au même endroit du code.
    /// </remarks>
    private static bool DialablePar(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
}

/// <summary>
/// Sonde par ouverture de connexion TCP sur l'hôte de la plateforme.
/// </summary>
public sealed class SondeTcp(Uri adresse, TimeSpan delai) : ISondeReseau
{
    /// <summary>Ce qui est éprouvé : hôte et port, port du schéma compris.</summary>
    public string Cible
    {
        get
        {
            var port = adresse.Port > 0 ? adresse.Port : adresse.Scheme == "https" ? 443 : 80;
            return $"{adresse.Host}:{port}";
        }
    }

    public async Task<bool> JoignableAsync(CancellationToken cancellation = default) =>
        (await EprouverAsync(cancellation)).Joignable;

    public async Task<ResultatSonde> EprouverAsync(CancellationToken cancellation = default)
    {
        try
        {
            using var client = new TcpClient();
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            limite.CancelAfter(delai);

            var port = adresse.Port > 0 ? adresse.Port : adresse.Scheme == "https" ? 443 : 80;
            await client.ConnectAsync(adresse.Host, port, limite.Token);
            return new ResultatSonde(client.Connected, Cible, "connexion ouverte");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // L'agent s'arrête : ce n'est pas un diagnostic réseau.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Le délai de la sonde, pas l'arrêt du service. Un délai qui expire
            // sans refus explicite ressemble à un filtrage silencieux ; un refus
            // immédiat ressemble à un port fermé. Les deux se soignent
            // différemment, donc ils se disent différemment.
            return new ResultatSonde(false, Cible, $"aucune réponse en {delai.TotalSeconds:0.#} s");
        }
        catch (Exception erreur)
        {
            // Toute autre issue — DNS, refus — se lit de la même façon pour la
            // décision d'envoi : on ne sait pas joindre, donc on n'envoie pas.
            // Mais pas pour le diagnostic, d'où le détail conservé.
            return new ResultatSonde(false, Cible, erreur.GetType().Name + " : " + erreur.Message.Trim());
        }
    }
}

/// <summary>Sonde qui répond toujours la même chose. Pour les essais.</summary>
public sealed class SondeFigee(bool joignable) : ISondeReseau
{
    public Task<bool> JoignableAsync(CancellationToken cancellation = default) =>
        Task.FromResult(joignable);

    public Task<ResultatSonde> EprouverAsync(CancellationToken cancellation = default) =>
        Task.FromResult(new ResultatSonde(joignable, "sonde figée", "valeur d'essai"));
}
