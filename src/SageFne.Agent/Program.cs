using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SageFne.Agent;
using SageFne.Agent.Configuration;
using SageFne.Agent.Journalisation;
using SageFne.Agent.Sante;
using SageFne.Core.Configuration;

// ---------------------------------------------------------------------------
// L'agent FNE : un service Windows, sans fenêtre, sans interface.
//
// Aucune console n'est jamais ouverte — ni au démarrage, ni à la détection
// d'une facture, ni à l'envoi. Le projet compile en WinExe sous Windows, ce qui
// l'interdit au niveau du binaire, et rien ici n'écrit sur la sortie standard :
// tout passe par le journal fichier.
//
// Le CLI SageFne.Reader reste entier et inchangé. Il sert au diagnostic et à la
// maintenance ; l'agent sert à la production. Les deux appliquent exactement les
// mêmes règles, parce qu'elles vivent toutes dans SageFne.Core.
// ---------------------------------------------------------------------------

var constructeur = Host.CreateApplicationBuilder(args);

// Fait de l'hôte un vrai service Windows : démarrage automatique confié au SCM,
// survie à la déconnexion de l'utilisateur, arrêt propre à l'extinction. Sans
// effet ailleurs, ce qui laisse le projet compilable et testable sur toute
// plateforme.
constructeur.Services.AddWindowsService(options =>
{
    options.ServiceName = "SageFne Agent";
});

constructeur.Configuration.AddUserSecrets<ServiceSurveillance>(optional: true);

constructeur.Services.Configure<AgentOptions>(
    constructeur.Configuration.GetSection(AgentOptions.Section));

var chaineSage = constructeur.Configuration.GetConnectionString("Sage") ?? "";
var connexionConfiguree = ServicesMiddleware.ConnexionRenseignee(chaineSage);

// Le registre des certifications est la seule protection contre le doublon, et
// il doit survivre au redémarrage du service. Sans connexion Sage renseignée,
// il reste en mémoire — l'agent tourne alors sur le jeu d'essai et ne peut rien
// certifier de réel.
var cheminRegistre = ServicesMiddleware.CheminRegistre(
    null,
    constructeur.Configuration["Fne:CertificationLedgerPath"],
    AppContext.BaseDirectory,
    connexionConfiguree);

// Le câblage métier, exactement celui du CLI. Rien n'est redéclaré ici : une
// règle qui vivrait en double finirait par diverger, et c'est la certification
// qui en paierait le prix.
constructeur.Services.AjouterMiddlewareFne(
    constructeur.Configuration, chaineSage, cheminRegistre);

// --- Journal ---------------------------------------------------------------

var reglagesJournal = constructeur.Configuration
    .GetSection(AgentOptions.Section).Get<AgentOptions>() ?? new AgentOptions();

var dossierJournal = string.IsNullOrWhiteSpace(reglagesJournal.CheminJournal)
    ? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SageFne", "journaux")
    : reglagesJournal.CheminJournal;

// La console disparaît des fournisseurs : un service qui écrirait dessus
// ouvrirait la fenêtre que l'exigence interdit.
constructeur.Logging.ClearProviders();
constructeur.Logging.AddProvider(new JournalFichier(dossierJournal, reglagesJournal.RetentionJournalJours));

// L'Event Log n'existe que sous Windows ; le reste du service tourne partout.
if (OperatingSystem.IsWindows()) constructeur.Logging.AjouterEventLog();

// --- Santé -----------------------------------------------------------------

constructeur.Services.AddSingleton<IPublicationHeartbeat, HeartbeatJournal>();

constructeur.Services.AddSingleton<ISondeReseau>(services =>
{
    var api = services.GetRequiredService<IOptions<FneApiOptions>>().Value;

    // Sans adresse configurée, rien n'est joignable — et c'est la bonne
    // réponse : une sonde qui répondrait « oui » par défaut ferait entrer
    // l'agent dans le chemin d'envoi avec une configuration vide.
    return Uri.TryCreate(api.BaseUrl, UriKind.Absolute, out var adresse)
        ? new SondeTcp(adresse, TimeSpan.FromSeconds(5))
        : new SondeFigee(false);
});

constructeur.Services.AddHostedService<ServiceSurveillance>();

await constructeur.Build().RunAsync();
