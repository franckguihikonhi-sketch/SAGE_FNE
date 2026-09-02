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
    // FneApiOptions, et non IOptions<FneApiOptions> : c'est l'instance liée par
    // AjouterMiddlewareFne que le reste de l'application utilise. Personne
    // n'appelle Configure<FneApiOptions>, si bien qu'un IOptions<> rendait un
    // objet neuf, aux valeurs par défaut - BaseUrl vide et Environment=Test.
    // Le battement annonçait donc « env=TEST » quelle que soit la configuration,
    // y compris sur un agent qui certifierait pour de vrai.
    FneApiOptions api,
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

        if (_reglages.Mode == ModeAgent.Automatic)
        {
            // Le passage en Automatic doit se voir dans le journal, en toutes
            // lettres et à chaque démarrage. Un service qui certifie sans qu'on
            // le lui demande ne doit jamais pouvoir être découvert après coup.
            logger.LogWarning(
                "Mode AUTOMATIC : les factures conformes et stables partiront d'elles-mêmes, " +
                "au plus {Plafond} par tour. Aucune confirmation ne sera demandée. " +
                "Pour revenir en arrière : variable machine Agent__Mode = Manual, " +
                "puis redémarrage du service.",
                Math.Max(1, _reglages.LimiteEnvoisParTour));
        }
        else
        {
            logger.LogInformation(
                "Mode {Mode} : aucune facture ne partira d'elle-même. Passez en Automatic " +
                "pour l'autoriser.", _reglages.Mode);
        }

        // Ce que la machine porte contre ce que ce processus applique. Sans
        // cette comparaison, un réglage posé et jamais vu par le service reste
        // indétectable : on attend un délai de 2 minutes qui en vaut 5, et l'on
        // conclut que l'automatisme ne marche pas.
        if (OperatingSystem.IsWindows())
        {
            var applique = new Dictionary<string, string>
            {
                ["Agent__Mode"] = _reglages.Mode.ToString(),
                ["Agent__StabiliteMinutes"] = _reglages.StabiliteMinutes.ToString(),
                ["Agent__FenetreJours"] = _reglages.FenetreJours.ToString(),
                ["Agent__IntervalleSecondes"] = _reglages.IntervalleSecondes.ToString(),
                ["Agent__LimiteEnvoisParTour"] = _reglages.LimiteEnvoisParTour.ToString(),
            };

            foreach (var ecart in EcartsEnvironnement.Detecter(
                         applique,
                         nom => Environment.GetEnvironmentVariable(nom, EnvironmentVariableTarget.Machine)))
            {
                logger.LogWarning("Réglage non appliqué : {Ecart}", ecart);
            }
        }

        // Sur quelles données il travaille, dit une fois au démarrage. Sans
        // cette ligne, un service qui tourne sur le jeu d'essai est
        // indiscernable d'un service qui certifie pour de vrai : même journal,
        // mêmes décisions, et pas une facture qui parte réellement.
        //
        // Le cas n'est pas théorique. Un service ne démarre pas sous le compte
        // qui l'installe, et le gestionnaire de services garde en cache
        // l'environnement machine tel qu'il était à l'amorçage de Windows : une
        // variable posée cinq minutes plus tôt peut lui rester invisible.
        using (var portee = fabrique.CreateScope())
        {
            var depot = portee.ServiceProvider.GetRequiredService<ISageInvoiceRepository>();
            if (depot is SageInvoiceRepository)
            {
                logger.LogInformation("Source des données : dossier SAGE (SQL Server), en lecture seule.");
            }
            else
            {
                logger.LogCritical(
                    "Aucune chaîne de connexion Sage : la lecture porte sur le JEU D'ESSAI, pas " +
                    "sur votre dossier. Rien de réel ne sera certifié. Posez " +
                    "ConnectionStrings__Sage en variable d'environnement MACHINE — et si elle " +
                    "y est déjà, redémarrez le poste : le gestionnaire de services ne voit pas " +
                    "les variables posées après l'amorçage.");
            }
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

    /// <summary>
    /// Un passage de vérification : lit, décide, et n'envoie rien.
    /// </summary>
    /// <remarks>
    /// Sert à éprouver un paramétrage avant d'installer quoi que ce soit. Deux
    /// tours avec un délai de stabilité nul : le premier voit les pièces pour la
    /// première fois, le second les retrouve inchangées. On sait alors que la
    /// lecture, les contrôles et la stabilité fonctionnent, sans attendre.
    ///
    /// Le mode est forcé à <see cref="ModeAgent.Manual"/> : une vérification qui
    /// certifierait une facture ne serait pas une vérification.
    /// </remarks>
    public async Task<IReadOnlyList<DecisionAgent>> VerifierAsync(CancellationToken arret = default)
    {
        using var portee = fabrique.CreateScope();
        var lecteur = portee.ServiceProvider.GetRequiredService<InvoiceBatchReader>();

        // La question qui précède toutes les autres. « 0 pièce lue » ne veut pas
        // dire la même chose selon qu'on interroge le vrai dossier ou le jeu
        // d'essai, et sans le dire on chercherait une panne là où il n'y a
        // qu'une chaîne de connexion absente.
        var depot = portee.ServiceProvider.GetRequiredService<ISageInvoiceRepository>();
        var surDonneesReelles = depot is SageInvoiceRepository;

        if (!surDonneesReelles)
        {
            logger.LogWarning(
                "Aucune chaîne de connexion Sage : la lecture porte sur le JEU D'ESSAI, pas sur " +
                "votre dossier. Posez ConnectionStrings__Sage en variable d'environnement " +
                "MACHINE — un service ne voit pas les secrets utilisateur.");
        }

        var depuis = DateTime.Today.AddDays(-Math.Max(1, _reglages.FenetreJours));
        var requete = new InvoiceQuery
        {
            Depuis = depuis,
            Limite = Math.Max(1, _reglages.LimiteParTour),
        };

        logger.LogInformation(
            "Lecture depuis le {Depuis:dd/MM/yyyy} — fenêtre de {Jours} jour(s), limite {Limite}.",
            depuis, _reglages.FenetreJours, _reglages.LimiteParTour);

        var moteur = new MoteurSurveillance(
            lecteur, new VerificateurStabilite(TimeSpan.Zero), ModeAgent.Manual);

        await moteur.ExaminerAsync(requete, arret);
        var decisions = await moteur.ExaminerAsync(requete, arret);

        // Les compteurs du battement doivent dire la même chose que la ligne
        // qui les précède. Sur le premier essai réel, le journal annonçait
        // « 200 pièces examinées » et le battement « examinees=0 » deux lignes
        // plus bas : deux nombres pour un même fait, et l'on ne sait plus
        // lequel croire.
        _examinees += decisions.Count;
        _enAttente = decisions.Count(decision =>
            decision.Motif is MotifAttente.ModeNonAutomatique or MotifAttente.NonConforme);

        foreach (var decision in decisions)
        {
            logger.LogInformation("{Explication}", decision.Explication);
        }

        var essai = await sonde.EprouverAsync(arret);
        _sage = EtatLien.Disponible;
        _reseau = essai.Joignable ? EtatLien.Disponible : EtatLien.Indisponible;

        // Les pièces hors périmètre comptent dans « lues » mais pas dans le
        // reste : sans cette distinction, un dossier dont l'historique est
        // écarté afficherait « 200 pièces lues, 0 prête » et ressemblerait à
        // une panne. Elles ne sont mentionnées que s'il y en a — une ligne qui
        // dit « dont 0 hors périmètre » n'apprend rien.
        var horsPerimetre = decisions.Count(
            decision => decision.Motif == MotifAttente.HorsPerimetre);

        logger.LogInformation(
            "Source {Source} : {Total} pièce(s) lues sur la fenêtre{Ecartees}, dont {Prets} que " +
            "seul le mode retient — en Automatic, elles partiraient.",
            surDonneesReelles ? "SAGE" : "jeu d'essai",
            decisions.Count,
            horsPerimetre == 0
                ? ""
                : $", dont {horsPerimetre} antérieure(s) au démarrage FNE et écartée(s)",
            decisions.Count(decision => decision.Motif == MotifAttente.ModeNonAutomatique));

        // Sur sa propre ligne, et en disant ce qui a été éprouvé. « Plateforme
        // FNE : INJOIGNABLE » collé en fin de phrase s'est lu comme « la DGI est
        // en panne » alors que la sonde n'avait établi qu'une chose : un socket
        // vers un hôte et un port ne s'est pas ouvert.
        if (essai.Joignable)
        {
            logger.LogInformation("Plateforme FNE joignable : {Explication}.", essai.Explication);
        }
        else
        {
            logger.LogWarning("Plateforme FNE : {Explication}", essai.Explication);
        }

        if (decisions.Count == 0)
        {
            logger.LogWarning(
                "Aucune pièce sur les {Jours} derniers jours. Ce n'est pas forcément une panne : " +
                "élargissez Agent:FenetreJours pour en lire davantage.", _reglages.FenetreJours);
        }
        else if (decisions.Count >= requete.Limite)
        {
            // « 200 pièces lues » quand la limite vaut 200 ne dit pas qu'il n'y
            // en a que 200 : il n'y en a pas eu davantage de lues. Sans cette
            // ligne, on croit avoir vu tout le dossier.
            logger.LogWarning(
                "La limite de {Limite} pièces par tour est atteinte : le dossier en contient " +
                "probablement d'autres sur cette fenêtre. Agent:LimiteParTour les découvrira.",
                requete.Limite);
        }

        await BattreAsync(arret);
        return decisions;
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

        var pretes = decisions.Where(decision => decision.Envoyable).ToList();
        if (pretes.Count == 0) return;

        // Le plafond d'envois, distinct de celui de lecture. Ce qui dépasse
        // n'est pas perdu : il repassera au tour suivant, une minute plus tard,
        // le temps de voir au journal ce qui part.
        var plafond = Math.Max(1, _reglages.LimiteEnvoisParTour);
        var aEnvoyer = pretes.Take(plafond).ToList();

        if (pretes.Count > aEnvoyer.Count)
        {
            logger.LogWarning(
                "{Pretes} pièce(s) sont prêtes, {Plafond} partiront ce tour-ci : " +
                "Agent:LimiteEnvoisParTour borne ce qui peut être certifié d'un coup. " +
                "Les autres suivront aux tours suivants. Si ce nombre vous surprend, " +
                "arrêtez le service avant le prochain tour.",
                pretes.Count, aEnvoyer.Count);
        }

        // La joignabilité se vérifie AVANT d'entrer dans le chemin d'envoi.
        // Après le POST, plus rien ne distingue une coupure survenue avant de
        // celle survenue après — et dans le doute la pièce reste bloquée en
        // Sending. Mieux vaut ne pas créer le doute.
        if (!await sonde.JoignableAsync(arret))
        {
            _reseau = EtatLien.Indisponible;
            logger.LogWarning(
                "Plateforme injoignable : {Nombre} pièce(s) restent en file, rien n'est parti.",
                pretes.Count);
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
        // La joignabilité s'éprouve au battement, et pas seulement avant un
        // envoi. Sur le premier service réel, « reseau=Inconnu » est resté
        // indéfiniment : la sonde n'est appelée que lorsqu'une pièce est prête
        // à partir, et il n'y en avait aucune. Le champ disait vrai — rien
        // n'avait été éprouvé — mais un battement de santé qui n'apprend jamais
        // rien sur le réseau ne sert à rien, et l'on découvrirait une coupure
        // le jour où une facture attend.
        //
        // Le coût est un socket toutes les cinq minutes. La sonde ne porte
        // aucune clé et n'envoie aucune requête HTTP : elle ne peut rien
        // certifier par accident.
        try
        {
            var essai = await sonde.EprouverAsync(arret);
            _reseau = essai.Joignable ? EtatLien.Disponible : EtatLien.Indisponible;

            // Le détail n'entre pas dans le battement, qui ne porte ni adresse
            // ni nom : il va au journal, et seulement quand ça ne répond pas.
            if (!essai.Joignable) logger.LogWarning("Plateforme FNE : {Explication}", essai.Explication);
        }
        catch (OperationCanceledException) when (arret.IsCancellationRequested)
        {
            throw;
        }

        var battement = new Heartbeat(
            AgentId: string.IsNullOrWhiteSpace(_reglages.AgentId)
                ? Environment.MachineName
                : _reglages.AgentId,
            CompanyId: _reglages.CompanyId,
            Version: typeof(ServiceSurveillance).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            Quand: DateTimeOffset.Now,
            Sage: _sage,
            Reseau: _reseau,
            Environnement: api.EstTest ? "TEST" : "PRODUCTION",
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
