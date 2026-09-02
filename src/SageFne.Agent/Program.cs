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

var hote = constructeur.Build();

// --- Le garde-fou d'installation -------------------------------------------
//
// Un service ne tourne pas sous le compte de celui qui l'installe. Deux
// mécanismes que le CLI utilise sans y penser s'en trouvent cassés : les
// secrets utilisateur, liés au profil, et le registre des certifications, dont
// le chemin par défaut passe par %APPDATA%. Le second est le pire : l'agent
// écrirait son registre ailleurs que le CLI, et deux mémoires pour une seule
// vérité finissent en doublon chez la DGI.
//
// Mieux vaut refuser de démarrer que de le laisser arriver en silence.
var journalDemarrage = hote.Services
    .GetRequiredService<ILoggerFactory>()
    .CreateLogger("Installation");

var empechements = GardeInstallation.Empechements(
    chaineSage,
    constructeur.Configuration["Fne:CertificationLedgerPath"],
    constructeur.Configuration["Fne:ApiKey"]);

foreach (var empechement in empechements)
{
    journalDemarrage.LogCritical("Démarrage refusé : {Empechement}", empechement);
}

if (empechements.Count > 0)
{
    journalDemarrage.LogCritical(
        "L'agent ne démarre pas. Le journal se trouve dans {Dossier}.", dossierJournal);
    return 1;
}

// --- Vérification, sans installer quoi que ce soit --------------------------
//
// « --verifier » fait un tour, écrit ce qu'il a vu au journal, et s'arrête.
// Sans lui, il n'y aurait aucun moyen d'éprouver le paramétrage : sous Windows
// le binaire est compilé sans console, et lancé à la main il ne dirait rien.
if (args.Contains("--verifier"))
{
    journalDemarrage.LogInformation(
        "Vérification : un tour, puis arrêt. Aucun service n'est installé.");

    await hote.StartAsync();
    await Task.Delay(TimeSpan.FromSeconds(20));
    await hote.StopAsync();

    journalDemarrage.LogInformation(
        "Vérification terminée. Lisez {Dossier} pour ce qui a été constaté.", dossierJournal);
    return 0;
}

await hote.RunAsync();
return 0;
