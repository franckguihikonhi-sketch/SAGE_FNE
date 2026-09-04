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

// Éprouver la base et l'identité, devant le client, sans rien écrire.
if (demande.Verifier)
{
    return await VerifierAsync(demande);
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

    // Avant d'écrire : ce poste appartient-il déjà à quelqu'un d'autre ? Une
    // réinstallation sur le même client est banale ; une installation par-
    // dessus un AUTRE client ne l'est pas.
    if (Reconnaissance.Avertissement(Reconnaissance.Lire(reglagesEnPlace ?? ""), demande) is { } alerte)
    {
        Console.WriteLine();
        Console.WriteLine("  ATTENTION");
        Console.WriteLine($"  {alerte}");
        Console.WriteLine();
    }

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

/// <summary>
/// Ce que ce poste contient, et quelle identité y serait posée. N'écrit rien.
/// </summary>
/// <remarks>
/// À passer chez le client, devant lui, avant d'installer. Ce qui s'y voit ne
/// se voit nulle part ailleurs : la base Sage réellement jointe, ce qu'elle
/// contient, et l'identité FNE qu'on s'apprête à poser. Les clients ne
/// partagent aucun accès FNE, et se tromper de poste ferait certifier sous le
/// mauvais NCC.
/// </remarks>
async Task<int> VerifierAsync(Demande demande)
{
    Titre("Ce poste");

    var fichier = Path.Combine(demande.Destination, "appsettings.json");
    var enPlace = Reconnaissance.Lire(File.Exists(fichier) ? File.ReadAllText(fichier) : "");

    if (enPlace is { Renseignee: true })
    {
        Console.WriteLine($"  Identité déjà posée   {enPlace.PointDeVente} / {enPlace.Etablissement}");
        Console.WriteLine($"  Registre en place     {(enPlace.Registre == "" ? "(non renseigné)" : enPlace.Registre)}");
    }
    else
    {
        Console.WriteLine("  Aucune installation antérieure.");
    }

    Titre("Ce qui serait posé");
    Console.WriteLine($"  Point de vente        {Renseigne(demande.PointDeVente)}");
    Console.WriteLine($"  Établissement         {Renseigne(demande.Etablissement)}");
    Console.WriteLine($"  Environnement         {(demande.Production ? "PRODUCTION" : "essai")}");
    Console.WriteLine($"  Clé d'API FNE         {(demande.CleFne == "" ? "(non fournie)" : Masquer(demande.CleFne))}");

    if (Reconnaissance.Avertissement(enPlace, demande) is { } alerte)
    {
        Titre("ATTENTION");
        Console.WriteLine($"  {alerte}");
    }

    Titre("La base Sage");

    if (!SageFne.Core.Configuration.ServicesMiddleware.ConnexionRenseignee(demande.ChaineSage))
    {
        Console.WriteLine("  Aucune chaîne de connexion exploitable : rien n'a été joint.");
        Console.WriteLine("  Donnez --sage pour que cette vérification serve à quelque chose.");
        return 1;
    }

    try
    {
        var depot = new SageFne.Core.Data.SageInvoiceRepository(
            demande.ChaineSage,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SageFne.Core.Data.SageInvoiceRepository>.Instance);

        var domaines = await depot.GetDomainesAsync();

        if (domaines.Count == 0)
        {
            Console.WriteLine("  Base jointe, mais aucun document dans F_DOCENTETE.");
            Console.WriteLine("  Est-ce bien le dossier du client ?");
            return 1;
        }

        Console.WriteLine($"  {"Domaine",-8} {"Type",-6} {"Documents",10}  Exemple");
        foreach (var ligne in domaines.OrderByDescending(d => d.Nombre).Take(8))
        {
            Console.WriteLine(
                $"  {ligne.Domaine,-8} {ligne.Type,-6} {ligne.Nombre,10}  " +
                $"pièce {ligne.Exemple} — {ligne.Tiers}");
        }

        Console.WriteLine();
        Console.WriteLine("  FAITES RECONNAÎTRE CES DOCUMENTS AU CLIENT. S'il ne reconnaît ni les");
        Console.WriteLine("  numéros de pièce ni les comptes tiers, la chaîne de connexion ne");
        Console.WriteLine("  désigne pas son dossier — et rien ne doit être installé.");
    }
    catch (Exception erreur)
    {
        Console.WriteLine($"  Base Sage injoignable : {erreur.Message}");
        Console.WriteLine();
        Console.WriteLine("  Vérifiez le serveur, le nom de la base, et que le compte SQL existe");
        Console.WriteLine("  et n'a que le rôle db_datareader.");
        return 1;
    }

    Titre("Rien n'a été écrit");
    Console.WriteLine("  Aucun service, aucun fichier, aucune variable machine.");
    Console.WriteLine("  Relancez sans --verifier pour installer.");
    return 0;
}

string Renseigne(string valeur) => valeur == "" ? "(non fourni)" : valeur;

/// <summary>La clé, reconnaissable sans être lisible.</summary>
string Masquer(string cle) => cle.Length <= 8
    ? new string('•', cle.Length)
    : $"{cle[..4]}{new string('•', 8)}{cle[^4..]}";

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
