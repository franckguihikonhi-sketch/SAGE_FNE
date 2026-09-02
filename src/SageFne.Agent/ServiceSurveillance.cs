using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SageFne.Agent.Configuration;
using SageFne.Agent.Sante;
using SageFne.Agent.Surveillance;
using SageFne.Core.Batch;
using SageFne.Core.Configuration;
using SageFne.Core.Data;
using SageFne.Core.Fne;
using Microsoft.Extensions.DependencyInjection;

namespace SageFne.Agent;

/// <summary>
/// Le tour de garde : lire, décider, et n'envoyer que ce que tout autorise.
/// </summary>
/// <remarks>
/// Ce service ne connaît aucune règle fiscale. Il enchaîne des composants qui
/// existaient déjà — <see cref="InvoiceBatchReader"/> pour la lecture et les
/// contrôles, <see cref="InvoiceSender"/> pour l'envoi et le registre — et
/// ajoute ce qu'un humain apportait jusqu'ici : le jugement qu'une saisie est
/// finie, et la patience de ne rien tenter quand la plateforme ne répond pas.
///
/// Ce qu'il ne fait jamais : réessayer un envoi dont l'issue est inconnue. Un
/// 5xx, un délai dépassé, une réponse illisible laissent la pièce en
/// <c>Sending</c>, et le lecteur refuse alors de la reproposer. C'est la seule
/// protection contre le doublon, et elle ne souffre pas d'exception.
/// </remarks>
public sealed class ServiceSurveillance(
    IServiceProvider fabrique,
    IOptions<AgentOptions> reglages,
    IOptions<FneApiOptions> api,
    ISondeReseau sonde,
    IPublicationHeartbeat battements,
    ILogger<ServiceSurveillance> logger) : BackgroundService
{
    private readonly AgentOptions _reglages = reglages.Value;

    private long _examinees;
    private long _envoyees;
    private DateTimeOffset? _derniereActivite;
    private EtatLien _sage = EtatLien.Inconnu;
    private EtatLien _reseau = EtatLien.Inconnu;
    private int _enAttente;

    /// <summary>Compteurs publics, pour les essais et le diagnostic.</summary>
    public long Examinees => _examinees;
    public long Envoyees => _envoyees;

    protected override async Task ExecuteAsync(CancellationToken arret)
    {
        logger.LogInformation(
            "Agent démarré — mode {Mode}, tour toutes les {Intervalle}, stabilité {Stabilite}.",
            _reglages.Mode, _reglages.Intervalle, _reglages.Stabilite);

        if (_reglages.Mode != ModeAgent.Automatic)
        {
            logger.LogInformation(
                "Mode {Mode} : aucune facture ne partira d'elle-même. Passez en Automatic " +
                "dans appsettings pour l'autoriser.", _reglages.Mode);
        }

        var stabilite = new VerificateurStabilite(_reglages.Stabilite);
        var prochainBattement = DateTimeOffset.MinValue;

        while (!arret.IsCancellationRequested)
        {
            try
            {
                await UnTourAsync(stabilite, arret);
            }
            catch (OperationCanceledException) when (arret.IsCancellationRequested)
            {
                break;
            }
            catch (Exception erreur)
            {
                // Un tour qui échoue ne doit pas arrêter le service : demain il
                // y aura d'autres factures, et un agent mort ne le dit à
                // personne.
                _sage = EtatLien.Indisponible;
                logger.LogError(erreur, "Tour de surveillance interrompu par une erreur.");
            }

            if (DateTimeOffset.Now >= prochainBattement)
            {
                await BattreAsync(arret);
                prochainBattement = DateTimeOffset.Now + _reglages.Heartbeat;
            }

            try
            {
                await Task.Delay(_reglages.Intervalle, arret);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation(
            "Agent arrêté — {Examinees} pièce(s) examinées, {Envoyees} envoyée(s).",
            _examinees, _envoyees);
    }

    private async Task UnTourAsync(VerificateurStabilite stabilite, CancellationToken arret)
    {
        // Une portée par tour : le registre, le dépôt Sage et le mapping se
        // relisent à chaque passage, et un paramétrage corrigé prend effet sans
        // redémarrer le service.
        using var portee = fabrique.CreateScope();
        var lecteur = portee.ServiceProvider.GetRequiredService<InvoiceBatchReader>();

        var requete = new InvoiceQuery
        {
            Depuis = DateTime.Today.AddDays(-Math.Max(1, _reglages.FenetreJours)),
            Limite = Math.Max(1, _reglages.LimiteParTour),
        };

        var moteur = new MoteurSurveillance(lecteur, stabilite, _reglages.Mode);
        var decisions = await moteur.ExaminerAsync(requete, arret);

        _sage = EtatLien.Disponible;
        _examinees += decisions.Count;
        _enAttente = decisions.Count(decision =>
            decision.Motif is MotifAttente.ModeNonAutomatique or MotifAttente.NonConforme);

        foreach (var decision in decisions.Where(decision => decision.Motif != MotifAttente.Aucun))
        {
            logger.LogInformation("{Explication}", decision.Explication);
        }

        var aEnvoyer = decisions.Where(decision => decision.Envoyable).ToList();
        if (aEnvoyer.Count == 0) return;

        // La joignabilité se vérifie AVANT d'entrer dans le chemin d'envoi.
        // Après le POST, plus rien ne distingue une coupure survenue avant de
        // celle survenue après — et dans le doute la pièce reste bloquée en
        // Sending. Mieux vaut ne pas créer le doute.
        if (!await sonde.JoignableAsync(arret))
        {
            _reseau = EtatLien.Indisponible;
            logger.LogWarning(
                "Plateforme injoignable : {Nombre} pièce(s) restent en file, rien n'est parti.",
                aEnvoyer.Count);
            return;
        }

        _reseau = EtatLien.Disponible;
        var expediteur = portee.ServiceProvider.GetRequiredService<InvoiceSender>();

        foreach (var decision in aEnvoyer)
        {
            if (arret.IsCancellationRequested) break;

            var resultat = await expediteur.EnvoyerAsync(decision.Piece, confirme: true, arret);
            _derniereActivite = DateTimeOffset.Now;

            if (resultat.Etat == EtatFne.Certified)
            {
                _envoyees++;
                stabilite.Oublier(decision.Identite);
                logger.LogInformation("Pièce {Piece} certifiée. {Message}", decision.Piece, resultat.Message);
                continue;
            }

            // Issue inconnue : la pièce reste en Sending et le lecteur refusera
            // de la reproposer. Aucun retry — c'est ainsi qu'un doublon se
            // fabrique, et il s'est déjà fabriqué une fois.
            logger.LogWarning(
                "Pièce {Piece} : {Etat}. {Message} Aucun renvoi automatique.",
                decision.Piece, resultat.Etat, resultat.Message);
        }
    }

    private async Task BattreAsync(CancellationToken arret)
    {
        var battement = new Heartbeat(
            AgentId: string.IsNullOrWhiteSpace(_reglages.AgentId)
                ? Environment.MachineName
                : _reglages.AgentId,
            CompanyId: _reglages.CompanyId,
            Version: typeof(ServiceSurveillance).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            Quand: DateTimeOffset.Now,
            Sage: _sage,
            Reseau: _reseau,
            Environnement: api.Value.EstTest ? "TEST" : "PRODUCTION",
            Mode: _reglages.Mode.ToString())
        {
            DerniereActivite = _derniereActivite,
            PiecesExaminees = _examinees,
            PiecesEnvoyees = _envoyees,
            EnAttente = _enAttente,
        };

        await battements.PublierAsync(battement, arret);
    }
}
