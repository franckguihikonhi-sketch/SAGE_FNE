using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SageFne.Core.Configuration;
using SageFne.Core.Saas;

namespace SageFne.Agent.Sante;

/// <summary>
/// Publie le battement vers la base d'audit, et relève qui d'autre bat.
/// </summary>
/// <remarks>
/// Deux raisons d'exister, et la seconde est la moins évidente.
///
/// La supervision d'abord : avec vingt clients, un agent tombé ne se voit pas.
/// Son journal est sur sa machine, et un journal muet ne se distingue pas d'un
/// service arrêté.
///
/// Et le constat d'exclusivité : en publiant, l'agent apprend combien d'autres
/// agents partagent son dossier. C'est ce fait — relevé, jamais déclaré — qui
/// décide de ce qu'il fera le jour où la base ne répondra plus.
/// </remarks>
public sealed class HeartbeatSaas(
    HttpClient http,
    OptionsSaas options,
    SuiviAgents suivi,
    ILogger<HeartbeatSaas> logger) : IPublicationHeartbeat
{
    /// <summary>Au-delà, un agent est tenu pour éteint.</summary>
    private static readonly TimeSpan Fraicheur = TimeSpan.FromMinutes(15);

    public async Task PublierAsync(Heartbeat battement, CancellationToken cancellation = default)
    {
        if (!options.Actif) return;

        try
        {
            if (await EcrireAsync(battement, cancellation))
            {
                suivi.Noter(await CompterLesAutresAsync(battement.AgentId, cancellation));
            }
        }
        catch (Exception erreur) when (erreur is not OperationCanceledException)
        {
            // Ne rien publier est sans conséquence sur la certification : le
            // suivi garde son dernier constat, et l'agent continue.
            logger.LogWarning("Battement non publié vers la base d'audit : {Pourquoi}", erreur.Message);
        }
    }

    private async Task<bool> EcrireAsync(Heartbeat battement, CancellationToken cancellation)
    {
        var ligne = new Dictionary<string, object?>
        {
            ["dossier_id"] = options.DossierId,
            ["agent_id"] = battement.AgentId,
            ["quand"] = DateTimeOffset.Now,
            ["version"] = battement.Version,
            ["poste"] = SageFne.Core.Saas.Agent.Identifiant,
            ["environnement"] = battement.Environnement.Equals("production", StringComparison.OrdinalIgnoreCase)
                ? "production" : "test",
            ["mode"] = battement.Mode,
            ["sage"] = battement.Sage.ToString(),
            ["reseau"] = battement.Reseau.ToString(),
            ["examinees"] = battement.PiecesExaminees,
            ["envoyees"] = battement.PiecesEnvoyees,
            ["en_attente"] = battement.EnAttente,
            ["derniere_activite"] = battement.DerniereActivite,
        };

        using var requete = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{options.AdresseTable("battements")}?on_conflict=dossier_id,agent_id"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new[] { ligne }), Encoding.UTF8, "application/json"),
        };

        Poser(requete);
        requete.Headers.TryAddWithoutValidation("Prefer", "resolution=merge-duplicates,return=minimal");

        using var reponse = await http.SendAsync(requete, cancellation);
        return reponse.IsSuccessStatusCode;
    }

    /// <summary>Combien d'agents, autres que celui-ci, ont battu récemment.</summary>
    private async Task<int> CompterLesAutresAsync(string moi, CancellationToken cancellation)
    {
        var depuis = DateTimeOffset.UtcNow - Fraicheur;

        var adresse = new Uri(
            $"{options.AdresseTable("battements")}" +
            $"?dossier_id=eq.{Uri.EscapeDataString(options.DossierId)}" +
            $"&agent_id=neq.{Uri.EscapeDataString(moi)}" +
            $"&quand=gte.{Uri.EscapeDataString(depuis.ToString("O"))}" +
            "&select=agent_id");

        using var requete = new HttpRequestMessage(HttpMethod.Get, adresse);
        Poser(requete);

        using var reponse = await http.SendAsync(requete, cancellation);

        if (!reponse.IsSuccessStatusCode)
        {
            // Publier a réussi mais lire a échoué : ne rien noter vaut mieux
            // que de noter « seul » sur une réponse qu'on n'a pas eue.
            throw new HttpRequestException(
                $"lecture des battements refusée ({(int)reponse.StatusCode}).");
        }

        var corps = await reponse.Content.ReadAsStringAsync(cancellation);
        using var document = JsonDocument.Parse(corps);
        return document.RootElement.GetArrayLength();
    }

    private void Poser(HttpRequestMessage requete)
    {
        requete.Headers.TryAddWithoutValidation("apikey", options.CleService);
        requete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.CleService);
        requete.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
}
