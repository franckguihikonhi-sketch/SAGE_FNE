using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Versioning;
using SageFne.Core.Configuration;
using SageFne.Installeur;

// Installateur du middleware FNE. Console à dessein : contrairement à l'agent,
// qui ne doit jamais ouvrir de fenêtre, celui-ci est lancé exprès et doit
// montrer ce qu'il fait.

var analyse = LigneDeCommande.Lire(args);

if (analyse.AideDemandee)
{
    Console.WriteLine(LigneDeCommande.Aide);
    return 0;
}

if (analyse.Erreurs.Count > 0)
{
    foreach (var erreur in analyse.Erreurs) Console.Error.WriteLine($"  {erreur}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  SageFneSetup.exe --aide");
    return 2;
}

Titre("Middleware FNE - installation");

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("  Cet installateur ne vaut que pour Windows.");
    return 2;
}

var charge = Charge();
var demande = analyse.Demande;

if (demande.Desinstaller)
{
    if (!EstAdministrateur())
    {
        TitreErreur("Rien n'a été retiré");
        Console.Error.WriteLine(
            "  Retirer un service Windows demande les droits d'administrateur.");
        return 1;
    }

    return Desinstaller(demande);
}

if (!demande.Silencieux)
{
    demande = Demander(demande);
}

var empechements = Controles.Empechements(demande, charge is not null);

if (empechements.Count > 0)
{
    // Avant la première écriture, toujours. Une installation qui s'arrête au
    // milieu laisse un poste dans un état que personne n'a voulu.
    TitreErreur("Rien n'a été installé");
    foreach (var manque in empechements) Console.Error.WriteLine($"  - {manque}");
    return 1;
}

if (!EstAdministrateur())
{
    TitreErreur("Rien n'a été installé");
    Console.Error.WriteLine(
        "  Cette installation pose un service Windows et des variables machine :\n" +
        "  elle demande les droits d'administrateur. Relancez cet exécutable par\n" +
        "  un clic droit, « Exécuter en tant qu'administrateur ».");
    return 1;
}

if (demande.Simulation)
{
    Titre("Simulation");
    Console.WriteLine($"  Agent            {demande.Destination}");
    Console.WriteLine($"  Registre         {demande.Registre}");
    Console.WriteLine($"  Journaux         {demande.Journaux}");
    Console.WriteLine($"  Service          {demande.NomService}");
    Console.WriteLine($"  Environnement    {(demande.Production ? "PRODUCTION" : "essai")}");
    Console.WriteLine($"  Point de vente   {demande.PointDeVente}");
    Console.WriteLine($"  Établissement    {demande.Etablissement}");
    Console.WriteLine($"  Écran distant    {(demande.SaasDemande ? demande.SupabaseUrl : "non configuré")}");
    Console.WriteLine();
    Console.WriteLine("  Rien n'a été écrit. Retirez --simulation pour installer.");
    return 0;
}

try
{
    return Installer(demande, charge!);
}
catch (Exception erreur)
{
    TitreErreur("L'installation s'est interrompue");
    Console.Error.WriteLine($"  {erreur.Message}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  Le service a pu rester arrêté. Relancez cet exécutable une fois la");
    Console.Error.WriteLine("  cause corrigée : une réinstallation conserve les réglages du poste.");
    return 1;
}

// --- Les étapes -------------------------------------------------------------

[SupportedOSPlatform("windows")]
int Installer(Demande demande, byte[] charge)
{
    Titre("Service");
    var tournait = ServiceExiste(demande.NomService);

    if (tournait)
    {
        // Il verrouille ses fichiers. Pendant ces quelques secondes, aucune
        // facture n'est examinée ni envoyée.
        Executer("sc.exe", $"stop \"{demande.NomService}\"", tolere: true);
        Thread.Sleep(TimeSpan.FromSeconds(3));
        Console.WriteLine("  Service arrêté le temps de remplacer les fichiers.");
    }
    else
    {
        Console.WriteLine("  Première installation sur ce poste.");
    }

    Titre("Fichiers");
    var ancien = Path.Combine(demande.Destination, "appsettings.json");
    var reglagesEnPlace = File.Exists(ancien) ? File.ReadAllText(ancien) : null;

    if (FusionReglages.Illisible(reglagesEnPlace))
    {
        Console.WriteLine("  L'ancien appsettings.json est illisible : les valeurs livrées");
        Console.WriteLine("  s'appliquent. Vérifiez les réglages affichés plus bas.");
    }

    Directory.CreateDirectory(demande.Destination);
    Directory.CreateDirectory(demande.Journaux);
    Directory.CreateDirectory(Path.GetDirectoryName(demande.Registre)!);

    using (var flux = new MemoryStream(charge))
    using (var archive = new ZipArchive(flux, ZipArchiveMode.Read))
    {
        archive.ExtractToDirectory(demande.Destination, overwriteFiles: true);
    }

    Console.WriteLine($"  Agent déposé dans {demande.Destination}");

    // Les réglages, fondus : ce que le poste portait l'emporte sur ce que la
    // livraison apporte. Un réglage qu'on cesse de porter est un réglage perdu,
    // et cela a déjà fait retomber la fenêtre de 30 jours à 7 sans un mot.
    var imposes = new Dictionary<string, string?>
    {
        ["Fne:CertificationLedgerPath"] = demande.Registre,
        ["Agent:CheminJournal"] = demande.Journaux,
        ["Fne:PointOfSale"] = demande.PointDeVente,
        ["Fne:Establishment"] = demande.Etablissement,
        ["Fne:Environment"] = demande.Production ? "Production" : "Test",
    };

    if (demande.SaasDemande)
    {
        imposes["Saas:Url"] = demande.SupabaseUrl;
        imposes["Saas:DossierId"] = demande.Dossier;
    }

    var livre = File.ReadAllText(Path.Combine(demande.Destination, "appsettings.json"));
    File.WriteAllText(ancien, FusionReglages.Fondre(livre, reglagesEnPlace, imposes));

    Titre("Secrets");

    // En variables machine, jamais dans appsettings.json : le fichier part dans
    // les sauvegardes et se lit par-dessus l'épaule. Le service, lui, ne tourne
    // pas sous le compte qui installe et ne voit pas ses secrets utilisateur.
    Poser("ConnectionStrings__Sage", demande.ChaineSage, "chaîne de connexion Sage");
    Poser("Fne__ApiKey", demande.CleFne, "clé d'API FNE");

    if (demande.SaasDemande)
    {
        Poser("Saas__CleService", demande.SupabaseCle, "clé de la base d'audit");
    }

    Titre("Enregistrement");
    var binaire = Path.Combine(demande.Destination, "SageFne.Agent.exe");

    if (!File.Exists(binaire))
    {
        throw new FileNotFoundException(
            $"L'agent est introuvable après extraction : {binaire}. Rien n'a été enregistré.");
    }

    if (tournait)
    {
        Executer("sc.exe", $"config \"{demande.NomService}\" binPath= \"{binaire}\" start= auto");
        Console.WriteLine("  Service mis à jour.");
    }
    else
    {
        Executer("sc.exe",
            $"create \"{demande.NomService}\" binPath= \"{binaire}\" start= auto " +
            "DisplayName= \"Middleware FNE (Sage - DGI)\"");
        Executer("sc.exe",
            $"description \"{demande.NomService}\" " +
            "\"Lit les factures de Sage en lecture seule et les certifie auprès de la DGI.\"",
            tolere: true);
        Console.WriteLine("  Service créé, démarrage automatique.");
    }

    Executer("sc.exe", $"start \"{demande.NomService}\"", tolere: true);

    Titre("Installé");
    Console.WriteLine($"  Environnement    {(demande.Production ? "PRODUCTION" : "essai")}");
    Console.WriteLine($"  Point de vente   {demande.PointDeVente}");
    Console.WriteLine($"  Établissement    {demande.Etablissement}");
    Console.WriteLine($"  Registre         {demande.Registre}");
    Console.WriteLine($"  Journaux         {demande.Journaux}");
    Console.WriteLine();
    Console.WriteLine("  Tableau de bord  http://localhost:5080");
    Console.WriteLine("                   La liste des factures et le bouton « Certifier ».");
    Console.WriteLine("                   Depuis ce poste uniquement.");
    Console.WriteLine();
    Console.WriteLine("  Le service démarre en mode Manual : il observe et n'envoie rien tant");
    Console.WriteLine("  qu'un humain n'a pas cliqué. Ouvrez le tableau de bord et regardez la");
    Console.WriteLine("  liste avant toute autre chose.");

    if (demande.SaasDemande)
    {
        Console.WriteLine();
        Console.WriteLine($"  Écran distant    {demande.SupabaseUrl}");
        Console.WriteLine("                   L'agent y reflète son registre à chaque tour.");
        Console.WriteLine("                   Le registre local reste la seule référence.");
    }

    Console.WriteLine();
    Console.WriteLine("  SAUVEGARDEZ le registre. Il est la seule mémoire des certifications :");
    Console.WriteLine("  le perdre ferait repartir à la DGI des factures déjà certifiées.");
    return 0;
}

/// <summary>
/// Retire le service et les fichiers, et laisse ce qui fait preuve.
/// </summary>
/// <remarks>
/// Le registre des certifications et les journaux restent. Ils disent ce qui a
/// été déclaré à la DGI, et une facture certifiée ne s'annule que par un avoir :
/// effacer sa trace parce qu'on désinstalle un logiciel serait perdre la seule
/// mémoire d'un fait fiscal. Leur sort revient au client, pas à l'installateur.
/// </remarks>
[SupportedOSPlatform("windows")]
int Desinstaller(Demande demande)
{
    Titre("Désinstallation");

    if (demande.Simulation)
    {
        Console.WriteLine($"  Le service {demande.NomService} serait arrêté puis retiré.");
        Console.WriteLine($"  {demande.Destination} serait supprimé.");
        Console.WriteLine("  Le registre et les journaux seraient conservés.");
        Console.WriteLine("  Rien n'a été fait. Retirez --simulation pour désinstaller.");
        return 0;
    }

    if (ServiceExiste(demande.NomService))
    {
        Executer("sc.exe", $"stop \"{demande.NomService}\"", tolere: true);
        Thread.Sleep(TimeSpan.FromSeconds(3));
        Executer("sc.exe", $"delete \"{demande.NomService}\"", tolere: true);
        Console.WriteLine($"  Service {demande.NomService} retiré.");
    }
    else
    {
        Console.WriteLine($"  Aucun service {demande.NomService} sur ce poste.");
    }

    foreach (var variable in new[] { "ConnectionStrings__Sage", "Fne__ApiKey", "Saas__CleService" })
    {
        Environment.SetEnvironmentVariable(variable, null, EnvironmentVariableTarget.Machine);
    }

    Console.WriteLine("  Secrets retirés des variables machine.");

    if (Directory.Exists(demande.Destination))
    {
        try
        {
            Directory.Delete(demande.Destination, recursive: true);
            Console.WriteLine($"  {demande.Destination} supprimé.");
        }
        catch (IOException erreur)
        {
            // Un fichier verrouillé n'annule pas la désinstallation : le
            // service est parti, c'est l'essentiel. Le dire vaut mieux que de
            // laisser croire que tout est propre.
            Console.WriteLine($"  {demande.Destination} n'a pas pu être supprimé : {erreur.Message}");
        }
    }

    Titre("Conservés, à dessein");
    Console.WriteLine($"  Registre   {demande.Registre}");
    Console.WriteLine($"  Journaux   {demande.Journaux}");
    Console.WriteLine();
    Console.WriteLine("  Le registre est la seule mémoire des certifications déjà faites.");
    Console.WriteLine("  Ne l'effacez qu'en connaissance de cause, et gardez-en une copie :");
    Console.WriteLine("  une facture certifiée ne s'annule que par un avoir.");
    return 0;
}

// --- Ce qui parle à Windows -------------------------------------------------

[SupportedOSPlatform("windows")]
void Poser(string variable, string valeur, string nom)
{
    Environment.SetEnvironmentVariable(variable, valeur, EnvironmentVariableTarget.Machine);

    // Jamais la valeur : cette sortie sera copiée dans un rapport.
    Console.WriteLine($"  {nom} : posée en variable machine.");
}

[SupportedOSPlatform("windows")]
bool EstAdministrateur()
{
    using var identite = System.Security.Principal.WindowsIdentity.GetCurrent();
    return new System.Security.Principal.WindowsPrincipal(identite)
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
}

bool ServiceExiste(string nom) => Executer("sc.exe", $"query \"{nom}\"", tolere: true) == 0;

int Executer(string programme, string arguments, bool tolere = false)
{
    using var processus = Process.Start(new ProcessStartInfo(programme, arguments)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    })!;

    processus.WaitForExit();

    if (processus.ExitCode != 0 && !tolere)
    {
        throw new InvalidOperationException(
            $"« {programme} {arguments} » a échoué ({processus.ExitCode}). " +
            processus.StandardError.ReadToEnd().Trim());
    }

    return processus.ExitCode;
}

// --- La charge utile et les questions ---------------------------------------

byte[]? Charge()
{
    using var flux = Assembly.GetExecutingAssembly()
        .GetManifestResourceStream("SageFne.Installeur.agent.zip");

    if (flux is null) return null;

    using var memoire = new MemoryStream();
    flux.CopyTo(memoire);
    return memoire.ToArray();
}

Demande Demander(Demande depart)
{
    Console.WriteLine("  Répondez à ce qui manque. Entrée pour garder ce qui est proposé.");
    Console.WriteLine();

    var resultat = depart with
    {
        ChaineSage = Question("Chaîne de connexion Sage (compte en LECTURE SEULE)", depart.ChaineSage),
        CleFne = QuestionMuette("Clé d'API FNE", depart.CleFne),
        PointDeVente = Question("Point de vente déclaré à la DGI", depart.PointDeVente),
        Etablissement = Question("Établissement déclaré à la DGI", depart.Etablissement),
    };

    Console.WriteLine();
    Console.WriteLine("  L'écran distant est facultatif. Entrée pour passer.");

    var url = Question("Adresse Supabase", depart.SupabaseUrl);

    return url == ""
        ? resultat
        : resultat with
        {
            SupabaseUrl = url,
            Dossier = Question("Identifiant du dossier", depart.Dossier),
            SupabaseCle = QuestionMuette("Clé de service Supabase", depart.SupabaseCle),
        };
}

string Question(string libelle, string defaut)
{
    Console.Write(defaut == "" ? $"  {libelle} : " : $"  {libelle} [{defaut}] : ");
    var lu = Console.ReadLine();
    return string.IsNullOrWhiteSpace(lu) ? defaut : lu.Trim();
}

/// <summary>Une saisie qui ne s'affiche pas : la personne derrière peut lire.</summary>
string QuestionMuette(string libelle, string defaut)
{
    Console.Write(defaut == "" ? $"  {libelle} : " : $"  {libelle} [inchangée] : ");

    var saisie = new System.Text.StringBuilder();

    while (true)
    {
        var touche = Console.ReadKey(intercept: true);

        if (touche.Key == ConsoleKey.Enter) break;

        if (touche.Key == ConsoleKey.Backspace)
        {
            if (saisie.Length > 0) saisie.Length--;
            continue;
        }

        if (!char.IsControl(touche.KeyChar)) saisie.Append(touche.KeyChar);
    }

    Console.WriteLine();
    return saisie.Length == 0 ? defaut : saisie.ToString();
}

void Titre(string texte)
{
    Console.WriteLine();
    Console.WriteLine(texte);
    Console.WriteLine(new string('-', texte.Length));
}

/// <summary>
/// Un titre sur la sortie d'erreur, avec ce qui le suit.
/// </summary>
/// <remarks>
/// Mélanger les deux flux entrelace leur affichage : le filet d'un titre écrit
/// sur la sortie standard s'est retrouvé au milieu d'une liste de motifs écrits
/// sur la sortie d'erreur. Ce qui explique un échec doit sortir d'un seul flux.
/// </remarks>
void TitreErreur(string texte)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine(texte);
    Console.Error.WriteLine(new string('-', texte.Length));
}
