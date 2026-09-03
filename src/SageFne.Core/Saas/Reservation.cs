using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SageFne.Core.Saas;

/// <summary>Ce qu'une réservation a donné.</summary>
public enum SortReservation
{
    /// <summary>Le SaaS n'est pas configuré : le registre local fait seul autorité.</summary>
    SansObjet,

    /// <summary>La pièce est à nous. Rien d'autre ne peut l'envoyer.</summary>
    Obtenue,

    /// <summary>Elle est déjà partie, ou en vol ailleurs. Ne rien envoyer.</summary>
    Refusee,

    /// <summary>La base n'a pas répondu : impossible de prouver que la pièce est libre.</summary>
    Indisponible,
}

/// <summary>
/// La mémoire anti-doublon partagée entre les postes.
/// </summary>
/// <remarks>
/// L'invariant n'est pas « un seul agent » : deux agents peuvent traiter deux
/// factures différentes du même dossier, et rien ne s'y oppose. Ce qui ne doit
/// jamais arriver, c'est que <b>la même pièce</b> parte deux fois.
///
/// Le registre fichier tient cette mémoire pour un poste. Il ne peut pas la
/// tenir pour deux : deux postes, deux registres qui s'ignorent, et la même
/// facture part deux fois — ce qui est arrivé sur ce dossier, et a demandé un
/// avoir. La réservation donne cette mémoire en partage, et c'est PostgreSQL
/// qui départage, par la contrainte d'unicité qui existe déjà.
///
/// Distinct du miroir, et volontairement : le miroir <b>reflète</b> après coup
/// et son échec ne doit rien empêcher, tandis que la réservation <b>décide</b>
/// avant l'envoi et son échec doit tout arrêter.
/// </remarks>
public interface IReservationClient
{
    bool Actif { get; }

    Task<SortReservation> ReserverAsync(
        string identite, string piece, CancellationToken cancellation = default);

    /// <summary>
    /// Rend une pièce après un refus <b>net</b> de la plateforme.
    /// </summary>
    /// <remarks>
    /// Jamais après une issue inconnue : la DGI a pu enregistrer la facture, et
    /// la rendre autoriserait un second envoi.
    /// </remarks>
    Task LibererAsync(string identite, string motif, CancellationToken cancellation = default);
}

/// <summary>Réserve par appel de fonction PostgreSQL, via PostgREST.</summary>
public sealed class ReservationHttp(
    HttpClient http,
    OptionsSaas options,
    Configuration.FneApiOptions api,
    ILogger<ReservationHttp> logger) : IReservationClient
{
    public bool Actif => options.Actif;

    public async Task<SortReservation> ReserverAsync(
        string identite, string piece, CancellationToken cancellation = default)
    {
        if (!options.Actif) return SortReservation.SansObjet;

        var reponse = await AppelerAsync("reserver_piece", new
        {
            p_dossier = options.DossierId,
            p_environnement = api.EstTest ? "test" : "production",
            p_identite = identite,
            p_piece = piece,
            p_agent = Agent.Identifiant,
        }, cancellation);

        return reponse switch
        {
            true => SortReservation.Obtenue,
            false => SortReservation.Refusee,
            _ => SortReservation.Indisponible,
        };
    }

    public async Task LibererAsync(
        string identite, string motif, CancellationToken cancellation = default)
    {
        if (!options.Actif) return;

        var rendu = await AppelerAsync("liberer_piece", new
        {
            p_dossier = options.DossierId,
            p_environnement = api.EstTest ? "test" : "production",
            p_identite = identite,
            p_motif = motif,
        }, cancellation);

        if (rendu is not true)
        {
            // Sans conséquence : la pièce reste réservée, donc bloquée, ce qui
            // est le bon sens de l'échec. Elle se débloque à la main.
            logger.LogInformation(
                "Pièce {Identite} : la libération partagée n'a pas abouti. Elle reste réservée.",
                identite);
        }
    }

    /// <summary>Vrai, faux, ou null quand la base n'a pas répondu.</summary>
    private async Task<bool?> AppelerAsync(
        string fonction, object corps, CancellationToken cancellation)
    {
        var adresse = new Uri(new Uri(options.Url.TrimEnd('/') + "/"), $"rest/v1/rpc/{fonction}");

        try
        {
            using var requete = new HttpRequestMessage(HttpMethod.Post, adresse)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(corps), Encoding.UTF8, "application/json"),
            };

            requete.Headers.TryAddWithoutValidation("apikey", options.CleService);
            requete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.CleService);
            requete.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var reponse = await http.SendAsync(requete, cancellation);

            if (!reponse.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Réservation partagée : {Fonction} a répondu {Code}.",
                    fonction, (int)reponse.StatusCode);
                return null;
            }

            var lu = (await reponse.Content.ReadAsStringAsync(cancellation)).Trim();
            return bool.TryParse(lu, out var verdict) ? verdict : null;
        }
        catch (Exception erreur) when (erreur is not OperationCanceledException)
        {
            logger.LogWarning("Réservation partagée injoignable : {Pourquoi}", erreur.Message);
            return null;
        }
    }
}

/// <summary>
/// L'identité de cet agent, stable d'un démarrage à l'autre.
/// </summary>
/// <remarks>
/// Le nom de la machine : c'est ce qu'un exploitant reconnaît, et c'est ce qui
/// permet de dire quel poste tient une pièce. Un identifiant tiré au hasard à
/// chaque démarrage ne dirait rien à personne.
/// </remarks>
public static class Agent
{
    public static string Identifiant { get; } = Nom();

    private static string Nom()
    {
        try
        {
            var machine = Environment.MachineName;
            return string.IsNullOrWhiteSpace(machine) ? "poste-inconnu" : machine;
        }
        catch (InvalidOperationException)
        {
            return "poste-inconnu";
        }
    }
}
