using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SageFne.Core.Saas;

/// <summary>
/// Publie vers PostgREST, l'interface HTTP que Supabase pose sur PostgreSQL.
/// </summary>
/// <remarks>
/// Un <c>POST</c> avec <c>Prefer: resolution=merge-duplicates</c> et
/// <c>on_conflict</c> sur la contrainte d'unicité : la même ligne republiée à
/// chaque tour met à jour au lieu de multiplier. C'est ce qui permet de publier
/// bêtement l'état courant sans tenir de file d'attente — et une file d'attente
/// serait une seconde mémoire, donc une seconde vérité.
/// </remarks>
public sealed class MiroirHttp(
    HttpClient http,
    OptionsSaas options,
    ILogger<MiroirHttp> logger) : IMiroirClient
{
    private static readonly JsonSerializerOptions Corps = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public bool Actif => options.Actif;

    public async Task<ResultatPublication> PublierAsync(
        IReadOnlyList<LigneMiroir> lignes, CancellationToken cancellation = default)
    {
        if (!options.Actif) return ResultatPublication.Inactif;
        if (lignes.Count == 0) return new ResultatPublication(0);

        using var requete = new HttpRequestMessage(HttpMethod.Post, Adresse())
        {
            Content = new StringContent(
                JsonSerializer.Serialize(lignes, Corps), Encoding.UTF8, "application/json"),
        };

        // La clé voyage en en-tête, deux fois : PostgREST veut « apikey », et
        // PostgreSQL veut le jeton porteur qui décide du rôle.
        requete.Headers.TryAddWithoutValidation("apikey", options.CleService);
        requete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.CleService);
        requete.Headers.TryAddWithoutValidation("Prefer", "resolution=merge-duplicates,return=minimal");
        requete.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var reponse = await http.SendAsync(requete, cancellation);

            if (reponse.IsSuccessStatusCode)
            {
                return new ResultatPublication(lignes.Count);
            }

            var corps = await reponse.Content.ReadAsStringAsync(cancellation);

            // Un refus de la base n'est pas une panne de réseau. Les
            // déclencheurs du schéma disent non à ce que le registre local
            // affirme : c'est un désaccord de fond, pas un incident de
            // transport, et il se lit dans le corps de la réponse.
            var refus = (int)reponse.StatusCode is >= 400 and < 500;

            logger.LogWarning(
                "Miroir : {Code} sur {Nombre} ligne(s) — {Detail}",
                (int)reponse.StatusCode, lignes.Count, Tronquer(corps, 500));

            return new ResultatPublication(
                0,
                refus ? lignes.Count : 0,
                $"la base d'audit a répondu {(int)reponse.StatusCode}.",
                Tronquer(corps, 500));
        }
        catch (TaskCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new ResultatPublication(0, Empechement: $"délai de {options.TimeoutSeconds} s dépassé.");
        }
        catch (HttpRequestException erreur)
        {
            return new ResultatPublication(0, Empechement: $"base d'audit injoignable : {erreur.Message}");
        }
    }

    /// <summary>L'adresse, avec la contrainte d'unicité visée par l'upsert.</summary>
    private Uri Adresse() => new(
        $"{options.AdresseCertifications()}?on_conflict=dossier_id,environnement,identite");

    private static string Tronquer(string texte, int maximum) =>
        texte.Length <= maximum ? texte : texte[..maximum] + "…";
}
