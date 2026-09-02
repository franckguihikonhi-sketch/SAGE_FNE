using System.Text;

namespace SageFne.Agent.Tests;

/// <summary>
/// Les scripts PowerShell du dépôt doivent être lisibles par Windows
/// PowerShell 5.1, celui qui est installé partout.
/// </summary>
/// <remarks>
/// Sans BOM, Windows PowerShell 5.1 lit un fichier en cp1252. Un tiret cadratin
/// « — », qui vaut E2 80 94 en UTF-8, y devient le caractère 0x94 — le guillemet
/// fermant typographique, que PowerShell traite comme un vrai délimiteur de
/// chaîne. Toutes les accolades se déséquilibrent alors, et l'erreur annoncée
/// est « accolade fermante manquante » cent lignes plus loin, à un endroit
/// parfaitement correct.
///
/// C'est arrivé sur le script d'installation, après que le même défaut d'encodage
/// eut déjà touché l'export CSV de la campagne NCC puis le journal de l'agent.
/// Trois fois la même cause : elle mérite un test plutôt qu'une vigilance.
///
/// PowerShell 7 lit l'UTF-8 par défaut : vérifier la syntaxe avec lui ne suffit
/// pas, et n'a pas suffi.
/// </remarks>
public class ScriptsWindowsTests
{
    private static IEnumerable<string> Scripts()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "SageFne.sln")))
        {
            dossier = dossier.Parent;
        }

        Assert.NotNull(dossier);
        return Directory.EnumerateFiles(dossier!.FullName, "*.ps1", SearchOption.AllDirectories)
            .Where(chemin => !chemin.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                          && !chemin.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    [Fact]
    public void Le_depot_porte_au_moins_un_script()
    {
        // Sans cela, le test suivant passerait au vert sur une liste vide le
        // jour où les scripts seraient déplacés.
        Assert.NotEmpty(Scripts());
    }

    [Fact]
    public void Tout_script_powershell_porte_un_BOM()
    {
        foreach (var script in Scripts())
        {
            var octets = File.ReadAllBytes(script);

            Assert.True(
                octets.Length >= 3 && octets[0] == 0xEF && octets[1] == 0xBB && octets[2] == 0xBF,
                $"{Path.GetFileName(script)} n'a pas de BOM : Windows PowerShell 5.1 le lira en " +
                "cp1252, et tout tiret cadratin y deviendra un guillemet.");
        }
    }

    [Fact]
    public void Aucun_script_ne_se_relit_en_guillemet_sous_cp1252()
    {
        // Le contrôle qui décrit vraiment la panne. Un BOM suffit aujourd'hui,
        // mais si quelqu'un le retire, mieux vaut nommer la conséquence que
        // constater une absence de préambule.
        var cp1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252);
        Assert.NotNull(cp1252);

        foreach (var script in Scripts())
        {
            var octets = File.ReadAllBytes(script);
            var sansBom = octets.Skip(3).ToArray();
            var relu = cp1252!.GetString(sansBom);

            var fantomes = relu.Count(caractere => caractere is '“' or '”' or '‘' or '’');

            Assert.True(
                fantomes == 0 || (octets.Length >= 3 && octets[0] == 0xEF),
                $"{Path.GetFileName(script)} produirait {fantomes} guillemet(s) fantôme(s) " +
                "sous cp1252, sans BOM pour l'en empêcher.");
        }
    }
}
