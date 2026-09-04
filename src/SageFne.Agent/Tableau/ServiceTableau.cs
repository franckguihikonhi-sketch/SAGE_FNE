using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SageFne.Agent.Configuration;

namespace SageFne.Agent.Tableau;

/// <summary>
/// Sert le tableau de bord sur la boucle locale, et rien d'autre.
/// </summary>
/// <remarks>
/// <b>Aucune fenêtre ne s'ouvre.</b> Ce service écoute un port ; c'est
/// l'exploitant qui décide d'ouvrir son navigateur. L'exigence « aucune console
/// » porte sur les fenêtres que le programme ferait apparaître, et il n'en
/// apparaît aucune.
///
/// <b>L'écoute est bornée à la boucle locale dans le code</b>, pas dans le
/// paramétrage. Le tableau ne demande aucun mot de passe : il porte le bouton
/// qui certifie, et une certification ne se défait pas. Un réglage qui pourrait
/// l'exposer au réseau serait un réglage qu'on finirait par mettre.
///
/// Le préfixe <c>localhost</c> plutôt que <c>127.0.0.1</c> : http.sys le
/// traite comme un cas particulier et n'exige pas de réservation d'URL, si bien
/// que la vérification à la main fonctionne sans élévation.
///
/// Une panne d'écoute n'arrête jamais l'agent. Le tableau est un confort ; la
/// certification est le métier. Un port déjà pris ne doit pas empêcher les
/// factures de partir.
/// </remarks>
public sealed class ServiceTableau(
    RouteurTableau routeur,
    IOptions<AgentOptions> reglages,
    ILogger<ServiceTableau> logger) : BackgroundService
{
    private readonly AgentOptions _reglages = reglages.Value;

    protected override async Task ExecuteAsync(CancellationToken arret)
    {
        if (!_reglages.TableauActif)
        {
            logger.LogInformation(
                "Tableau de bord désactivé (Agent:TableauActif). L'agent tourne sans écran.");
            return;
        }

        if (!HttpListener.IsSupported)
        {
            logger.LogWarning("Tableau de bord indisponible : HttpListener n'est pas pris en charge ici.");
            return;
        }

        var port = _reglages.TableauPort is > 0 and < 65536 ? _reglages.TableauPort : 5080;
        var adresse = $"http://localhost:{port}/";

        using var ecoute = new HttpListener();
        ecoute.Prefixes.Add(adresse);

        try
        {
            ecoute.Start();
        }
        catch (Exception erreur)
        {
            logger.LogError(erreur,
                "Tableau de bord : impossible d'écouter sur {Adresse}. Le port est peut-être " +
                "déjà pris — changez Agent:TableauPort. L'agent continue sans écran : " +
                "la certification n'en dépend pas.", adresse);
            return;
        }

        logger.LogInformation(
            "Tableau de bord ouvert sur {Adresse} — depuis ce poste uniquement. " +
            "Aucun mot de passe n'est demandé, et aucune machine du réseau ne peut l'atteindre.",
            adresse);

        // L'arrêt du service débloque l'attente : HttpListener.GetContextAsync
        // ignore le jeton d'annulation, et sans cela le service resterait
        // suspendu à une requête qui ne viendra pas.
        using var fermeture = arret.Register(
            () => { try { ecoute.Close(); } catch { /* déjà fermé */ } });

        while (!arret.IsCancellationRequested)
        {
            HttpListenerContext contexte;
            try
            {
                contexte = await ecoute.GetContextAsync();
            }
            catch (Exception) when (arret.IsCancellationRequested)
            {
                break;
            }
            catch (Exception erreur)
            {
                logger.LogWarning(erreur, "Tableau de bord : écoute interrompue.");
                break;
            }

            _ = ServirAsync(contexte, arret);
        }

        logger.LogInformation("Tableau de bord fermé.");
    }

    private async Task ServirAsync(HttpListenerContext contexte, CancellationToken arret)
    {
        try
        {
            // Ceinture et bretelles. Le préfixe « localhost » suffit déjà à
            // écarter le réseau ; cette vérification tient encore si quelqu'un
            // change le préfixe un jour sans mesurer ce qu'il ouvre.
            var pair = contexte.Request.RemoteEndPoint?.Address;
            if (pair is null || !IPAddress.IsLoopback(pair))
            {
                logger.LogWarning(
                    "Tableau de bord : requête refusée, venue de {Adresse}. " +
                    "Seul ce poste y a accès.", pair);
                await EcrireAsync(contexte, new ReponseHttp(403, "text/plain; charset=utf-8",
                    "Le tableau de bord n'est accessible que depuis le poste de l'agent."));
                return;
            }

            // Le corps n'est lu que pour un POST, et borné : le tableau ne
            // reçoit qu'un choix de mode de règlement, pas un téléversement.
            var corps = "";
            if (contexte.Request.HasEntityBody && contexte.Request.HttpMethod == "POST")
            {
                using var lecture = new StreamReader(
                    contexte.Request.InputStream, contexte.Request.ContentEncoding);

                var tampon = new char[4096];
                var lus = await lecture.ReadAsync(tampon, 0, tampon.Length);
                corps = new string(tampon, 0, lus);
            }

            var reponse = await routeur.RepondreAsync(
                contexte.Request.HttpMethod ?? "GET",
                contexte.Request.Url?.AbsolutePath ?? "/",
                corps,
                arret);

            await EcrireAsync(contexte, reponse);
        }
        catch (Exception erreur)
        {
            logger.LogError(erreur, "Tableau de bord : requête en échec.");
            try
            {
                await EcrireAsync(contexte, new ReponseHttp(500, "text/plain; charset=utf-8",
                    "L'agent n'a pas pu répondre. Le détail est au journal."));
            }
            catch { /* le client est parti */ }
        }
    }

    private static async Task EcrireAsync(HttpListenerContext contexte, ReponseHttp reponse)
    {
        var corps = Encoding.UTF8.GetBytes(reponse.Corps);
        contexte.Response.StatusCode = reponse.Code;
        contexte.Response.ContentType = reponse.TypeContenu;
        contexte.Response.ContentLength64 = corps.Length;

        // Le tableau ne se met pas en cache : une facture certifiée il y a
        // trente secondes doit apparaître certifiée.
        contexte.Response.Headers["Cache-Control"] = "no-store";

        await contexte.Response.OutputStream.WriteAsync(corps);
        contexte.Response.Close();
    }
}
