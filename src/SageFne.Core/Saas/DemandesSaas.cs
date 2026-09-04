using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SageFne.Core.Saas;

/// <summary>Un clic venu de l'écran distant, tel que la base le porte.</summary>
/// <remarks>
/// Ce n'est pas un ordre. L'agent le relit, refait tous ses contrôles, et
/// décide. Le registre local reste la seule autorité sur ce qui peut partir.
/// </remarks>
public sealed record DemandeSaas(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("identite")] string Identite,
    [property: JsonPropertyName("piece")] string Piece,
    [property: JsonPropertyName("mode_paiement")] string ModePaiement,
    [property: JsonPropertyName("demande_par")] string DemandePar = "");

/// <summary>Les demandes en attente, et la façon de les trancher.</summary>
public interface IDemandesClient
{
    bool Actif { get; }

    Task<IReadOnlyList<DemandeSaas>> EnAttenteAsync(int limite, CancellationToken cancellation = default);

    /// <summary>
    /// Réserve une demande. Faux quand une autre instance l'a prise avant.
    /// </summary>
    /// <remarks>
    /// La réservation est un <c>update</c> conditionné sur l'état
    /// <c>en_attente</c> : c'est PostgreSQL qui départage, pas nous. Deux
    /// agents sur le même dossier — un poste de secours, une migration en
    /// cours — ne peuvent pas envoyer la même facture deux fois.
    /// </remarks>
    Task<bool> PrendreAsync(string id, CancellationToken cancellation = default);

    Task TrancherAsync(string id, bool reussi, string resultat, CancellationToken cancellation = default);
}

/// <summary>Les demandes, lues et tranchées via PostgREST.</summary>
public sealed class DemandesHttp(
    HttpClient http,
    OptionsSaas options,
    ILogger<DemandesHttp> logger) : IDemandesClient
{
    private static readonly JsonSerializerOptions Lecture = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public bool Actif => options.Actif;

    public async Task<IReadOnlyList<DemandeSaas>> EnAttenteAsync(
        int limite, CancellationToken cancellation = default)
    {
        if (!options.Actif) return [];

        var adresse = new Uri(
            $"{options.AdresseTable("demandes_certification")}" +
            $"?dossier_id=eq.{Uri.EscapeDataString(options.DossierId)}" +
            "&etat=eq.en_attente&order=demande_le.asc" +
            $"&limit={Math.Clamp(limite, 1, 100)}" +
            "&select=id,identite,piece,mode_paiement,demande_par");

        try
        {
            using var requete = new HttpRequestMessage(HttpMethod.Get, adresse);
            Entetes.Poser(requete, options);

            using var reponse = await http.SendAsync(requete, cancellation);
            if (!reponse.IsSuccessStatusCode)
            {
                logger.LogWarning("Demandes SaaS : lecture refusée ({Code}).", (int)reponse.StatusCode);
                return [];
            }

            var corps = await reponse.Content.ReadAsStringAsync(cancellation);
            return JsonSerializer.Deserialize<List<DemandeSaas>>(corps, Lecture) ?? [];
        }
        catch (Exception erreur) when (erreur is not OperationCanceledException)
        {
            // Ne rien lire est sans conséquence : les demandes restent en base
            // et seront relues au tour suivant. Ce chemin ne doit jamais
            // empêcher l'agent de faire son travail habituel.
            logger.LogWarning("Demandes SaaS illisibles ce tour-ci : {Pourquoi}", erreur.Message);
            return [];
        }
    }

    public async Task<bool> PrendreAsync(string id, CancellationToken cancellation = default)
    {
        if (!options.Actif) return false;

        // Conditionné sur « en_attente » : si la ligne a changé entre la
        // lecture et ici, PostgreSQL ne met rien à jour et rend un tableau
        // vide. C'est la base qui départage, jamais un verrou en mémoire.
        var adresse = new Uri(
            $"{options.AdresseTable("demandes_certification")}" +
            $"?id=eq.{Uri.EscapeDataString(id)}&etat=eq.en_attente&select=id");

        var lignes = await PatchAsync(adresse, """{"etat":"prise"}""", cancellation);
        return lignes is not null && lignes.Count > 0;
    }

    public async Task TrancherAsync(
        string id, bool reussi, string resultat, CancellationToken cancellation = default)
    {
        if (!options.Actif) return;

        var adresse = new Uri(
            $"{options.AdresseTable("demandes_certification")}" +
            $"?id=eq.{Uri.EscapeDataString(id)}&select=id");

        var corps = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["etat"] = reussi ? "traitee" : "refusee",
            ["resultat"] = Tronquer(resultat, 2000),
        });

        if (await PatchAsync(adresse, corps, cancellation) is null)
        {
            // La facture est peut-être partie et la demande reste « prise ».
            // C'est le bon sens de l'échec : une demande bloquée se voit et se
            // règle à la main, là où une demande remise en attente repartirait
            // toute seule — et ferait un doublon.
            logger.LogWarning(
                "Demande {Id} : le verdict n'a pas pu être écrit. Elle reste « prise » et ne " +
                "sera pas rejouée.", id);
        }
    }

    private async Task<List<Dictionary<string, JsonElement>>?> PatchAsync(
        Uri adresse, string corps, CancellationToken cancellation)
    {
        try
        {
            using var requete = new HttpRequestMessage(HttpMethod.Patch, adresse)
            {
                Content = new StringContent(corps, Encoding.UTF8, "application/json"),
            };
            Entetes.Poser(requete, options);
            requete.Headers.TryAddWithoutValidation("Prefer", "return=representation");

            using var reponse = await http.SendAsync(requete, cancellation);
            if (!reponse.IsSuccessStatusCode) return null;

            var lu = await reponse.Content.ReadAsStringAsync(cancellation);
            return JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(lu) ?? [];
        }
        catch (Exception erreur) when (erreur is not OperationCanceledException)
        {
            logger.LogWarning("Demandes SaaS : écriture impossible — {Pourquoi}", erreur.Message);
            return null;
        }
    }

    private static string Tronquer(string texte, int maximum) =>
        texte.Length <= maximum ? texte : texte[..maximum] + "…";
}

/// <summary>
/// Les en-têtes que PostgREST attend, posés au même endroit pour tout le monde.
/// </summary>
/// <remarks>
/// Deux copies de ce code, et l'une finirait par oublier « apikey » ou par
/// glisser la clé ailleurs que dans un en-tête. Elle voyage là, et nulle part
/// dans un corps ni dans une URL.
/// </remarks>
internal static class Entetes
{
    public static void Poser(HttpRequestMessage requete, OptionsSaas options)
    {
        requete.Headers.TryAddWithoutValidation("apikey", options.CleService);
        requete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.CleService);
        requete.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
}
