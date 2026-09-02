using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using SageFne.Agent.Journalisation;

namespace SageFne.Agent.Tests;

/// <summary>
/// Ce que le service doit trouver au démarrage, et ce qu'il doit écrire.
/// </summary>
/// <remarks>
/// Deux défauts constatés au premier essai réel, tous deux invisibles à l'œil :
/// l'un aurait fait tourner l'agent sur des valeurs par défaut sans le dire,
/// l'autre rendait illisible le seul endroit où il parle.
/// </remarks>
public class DemarrageTests
{
    [Fact]
    public void L_hote_cherche_sa_configuration_a_cote_du_binaire()
    {
        // Un service lancé par Windows démarre dans C:\Windows\System32. Avec le
        // répertoire courant pour racine — le défaut de l'hôte — il n'y
        // trouverait jamais son appsettings.json et tournerait sur les valeurs
        // par défaut, en silence.
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "SageFne.sln")))
        {
            dossier = dossier.Parent;
        }

        var programme = File.ReadAllText(Path.Combine(
            dossier!.FullName, "src", "SageFne.Agent", "Program.cs"));

        Assert.Contains("ContentRootPath = AppContext.BaseDirectory", programme);
    }

    [Fact]
    public void Le_projet_embarque_son_appsettings()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "SageFne.sln")))
        {
            dossier = dossier.Parent;
        }

        var projet = XDocument.Load(Path.Combine(
            dossier!.FullName, "src", "SageFne.Agent", "SageFne.Agent.csproj"));

        Assert.Contains(projet.Descendants("None"), fichier =>
            fichier.Attribute("Update")?.Value == "appsettings.json"
            && fichier.Attribute("CopyToOutputDirectory")?.Value == "PreserveNewest");
    }

    [Fact]
    public void Le_journal_porte_un_BOM()
    {
        // Windows PowerShell lit un fichier sans BOM en ANSI, et rend
        // « Vérification » en « VÃ©rification ». Un journal illisible ne se lit
        // pas — et c'est le seul endroit où un service sans interface parle.
        var dossier = Path.Combine(Path.GetTempPath(), $"journal-bom-{Guid.NewGuid():N}");
        try
        {
            using var journal = new JournalFichier(dossier);
            journal.CreateLogger("Essai").LogInformation("Vérification terminée — accentué");

            var octets = File.ReadAllBytes(journal.FichierDuJour);

            Assert.True(octets.Length >= 3);
            Assert.Equal([0xEF, 0xBB, 0xBF], octets.Take(3));
            Assert.Contains("Vérification terminée — accentué", File.ReadAllText(journal.FichierDuJour));
        }
        finally
        {
            if (Directory.Exists(dossier)) Directory.Delete(dossier, recursive: true);
        }
    }

    [Fact]
    public void Le_BOM_ne_se_repete_pas_a_chaque_ligne()
    {
        var dossier = Path.Combine(Path.GetTempPath(), $"journal-bom-{Guid.NewGuid():N}");
        try
        {
            using var journal = new JournalFichier(dossier);
            var ecrivain = journal.CreateLogger("Essai");

            ecrivain.LogInformation("une");
            ecrivain.LogInformation("deux");
            ecrivain.LogInformation("trois");

            var octets = File.ReadAllBytes(journal.FichierDuJour);
            var bom = 0;
            for (var rang = 0; rang + 2 < octets.Length; rang++)
            {
                if (octets[rang] == 0xEF && octets[rang + 1] == 0xBB && octets[rang + 2] == 0xBF) bom++;
            }

            Assert.Equal(1, bom);
        }
        finally
        {
            if (Directory.Exists(dossier)) Directory.Delete(dossier, recursive: true);
        }
    }
}
