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
}

/// <summary>
/// Sonde par ouverture de connexion TCP sur l'hôte de la plateforme.
/// </summary>
public sealed class SondeTcp(Uri adresse, TimeSpan delai) : ISondeReseau
{
    public async Task<bool> JoignableAsync(CancellationToken cancellation = default)
    {
        try
        {
            using var client = new TcpClient();
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            limite.CancelAfter(delai);

            var port = adresse.Port > 0 ? adresse.Port : adresse.Scheme == "https" ? 443 : 80;
            await client.ConnectAsync(adresse.Host, port, limite.Token);
            return client.Connected;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // L'agent s'arrête : ce n'est pas un diagnostic réseau.
            throw;
        }
        catch (Exception)
        {
            // Toute autre issue — DNS, refus, délai — se lit de la même façon :
            // on ne sait pas joindre, donc on n'envoie pas.
            return false;
        }
    }
}

/// <summary>Sonde qui répond toujours la même chose. Pour les essais.</summary>
public sealed class SondeFigee(bool joignable) : ISondeReseau
{
    public Task<bool> JoignableAsync(CancellationToken cancellation = default) =>
        Task.FromResult(joignable);
}
