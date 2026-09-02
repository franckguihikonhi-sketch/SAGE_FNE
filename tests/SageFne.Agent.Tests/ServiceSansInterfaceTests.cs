using System.Reflection;
using System.Xml.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SageFne.Agent;
using SageFne.Agent.Configuration;
using SageFne.Agent.Journalisation;
using SageFne.Agent.Sante;
using SageFne.Agent.Surveillance;

namespace SageFne.Agent.Tests;

/// <summary>
/// L'agent tourne sans interface, et sans jamais ouvrir de fenêtre.
/// </summary>
/// <remarks>
/// L'exigence est absolue : aucune console ne doit apparaître, ni au démarrage,
/// ni à la détection d'une facture, ni à l'envoi. Ces tests vérifient ce qui
/// peut l'être hors de Windows — le type de sortie déclaré, l'absence de
/// journalisation console, et le fait que le service soit bien un service.
/// </remarks>
public class ServiceSansInterfaceTests
{
    private static XDocument Projet()
    {
        // Remonter depuis le binaire des tests jusqu'au csproj de l'agent : le
        // fichier de projet est ce qui décide du type de fenêtre, et il est donc
        // ce qu'il faut vérifier.
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "SageFne.sln")))
        {
            dossier = dossier.Parent;
        }

        Assert.NotNull(dossier);
        return XDocument.Load(Path.Combine(
            dossier!.FullName, "src", "SageFne.Agent", "SageFne.Agent.csproj"));
    }

    [Fact]
    public void Le_projet_compile_en_WinExe_sous_Windows()
    {
        // Un service compilé en Exe console ouvre une fenêtre dès qu'on le lance
        // à la main. « WinExe » l'interdit au niveau du binaire, avant toute
        // ligne de code — ce qu'aucune précaution en C# ne peut garantir seule.
        var sorties = Projet().Descendants("OutputType").ToList();

        Assert.Contains(sorties, sortie =>
            sortie.Value == "WinExe"
            && sortie.Attribute("Condition")?.Value.Contains("Windows_NT") == true);
    }

    [Fact]
    public void Le_projet_se_declare_service_windows()
    {
        var paquets = Projet().Descendants("PackageReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .ToList();

        Assert.Contains("Microsoft.Extensions.Hosting.WindowsServices", paquets);
    }

    [Fact]
    public void Le_service_est_un_service_heberge()
    {
        // BackgroundService : démarré par l'hôte, arrêté proprement par le SCM,
        // et survivant à la déconnexion de l'utilisateur.
        Assert.True(typeof(BackgroundService).IsAssignableFrom(typeof(ServiceSurveillance)));
        Assert.True(typeof(IHostedService).IsAssignableFrom(typeof(ServiceSurveillance)));
    }

    [Fact]
    public void Aucun_ecrivain_console_n_est_declare_dans_l_agent()
    {
        // Le journal passe par un fichier. Un fournisseur console rétabli par
        // mégarde rouvrirait la fenêtre que tout le reste s'emploie à éviter.
        var types = typeof(ServiceSurveillance).Assembly.GetTypes();

        Assert.DoesNotContain(types, type =>
            type.Name.Contains("Console", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(types, type => type == typeof(JournalFichier));
    }

    [Fact]
    public void Le_journal_ecrit_dans_un_fichier_et_pas_ailleurs()
    {
        var dossier = Path.Combine(Path.GetTempPath(), $"journal-{Guid.NewGuid():N}");
        try
        {
            using var journal = new JournalFichier(dossier);
            var ecrivain = journal.CreateLogger("Essai");

            ecrivain.LogInformation("une ligne de journal");

            Assert.True(File.Exists(journal.FichierDuJour));
            Assert.Contains("une ligne de journal", File.ReadAllText(journal.FichierDuJour));
        }
        finally
        {
            if (Directory.Exists(dossier)) Directory.Delete(dossier, recursive: true);
        }
    }

    [Fact]
    public void Le_journal_hors_d_atteinte_n_arrete_pas_l_agent()
    {
        // Un disque plein ne doit pas devenir une panne de certification.
        var dossier = Path.Combine(Path.GetTempPath(), $"journal-{Guid.NewGuid():N}");
        using var journal = new JournalFichier(dossier);
        var ecrivain = journal.CreateLogger("Essai");

        Directory.Delete(dossier, recursive: true);

        var exception = Record.Exception(() => ecrivain.LogWarning("écriture impossible"));

        Assert.Null(exception);
    }

    [Fact]
    public void Le_heartbeat_compte_ce_qu_il_annonce()
    {
        // Sur le premier essai réel, le journal annonçait « 200 pièces
        // examinées » et le battement « examinees=0 » deux lignes plus bas.
        // Deux nombres pour un même fait, et l'on ne sait plus lequel croire —
        // c'est le défaut qui revient le plus souvent dans ce projet.
        var battement = new Heartbeat(
            "POSTE-01", "F", "1.0.0", DateTimeOffset.Now,
            EtatLien.Disponible, EtatLien.Disponible, "TEST", "Manual")
        {
            PiecesExaminees = 200,
            EnAttente = 200,
        };

        Assert.Contains("examinees=200", battement.ToString());
        Assert.Contains("attente=200", battement.ToString());
    }

    [Fact]
    public void Le_heartbeat_ne_porte_ni_cle_ni_adresse()
    {
        // Il finira dans un fichier, un Event Log, puis une télémétrie SaaS. Ce
        // qui n'y entre pas n'en fuitera pas.
        var battement = new Heartbeat(
            "POSTE-01", "FISH-AFRIC", "1.0.0", DateTimeOffset.Now,
            EtatLien.Disponible, EtatLien.Disponible, "TEST", "Manual");

        var champs = typeof(Heartbeat).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(propriete => propriete.Name)
            .ToList();

        Assert.DoesNotContain(champs, nom => nom.Contains("Key", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(champs, nom => nom.Contains("Url", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(champs, nom => nom.Contains("Cle", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("POSTE-01", battement.ToString());
    }

    [Fact]
    public void Un_lien_non_eprouve_ne_se_lit_pas_comme_bon()
    {
        // Même règle que dans le registre : la place zéro ne doit rien affirmer.
        Assert.Equal(EtatLien.Inconnu, (EtatLien)0);

        var jamaisEprouve = new Heartbeat(
            "POSTE-01", "F", "1.0.0", DateTimeOffset.Now,
            EtatLien.Inconnu, EtatLien.Inconnu, "TEST", "Manual");

        Assert.False(jamaisEprouve.EnBonneSante);
    }
}
