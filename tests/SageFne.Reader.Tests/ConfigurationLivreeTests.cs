using Microsoft.Extensions.Configuration;
using SageFne.Core.Configuration;

namespace SageFne.Core.Tests;

/// <summary>
/// Ce que les appsettings.json livrés disent réellement, une fois liés.
/// </summary>
/// <remarks>
/// <c>DemarrageLe</c> est une <c>DateTime?</c> : une valeur mal écrite ne
/// provoque pas d'erreur de démarrage, elle se lie à <c>null</c> — et null veut
/// dire « aucun plancher, tout l'historique est candidat ». Le défaut le plus
/// silencieux possible : une faute de frappe rouvre mille quatre factures et
/// rien ne le dit.
///
/// Ces tests lisent les vrais fichiers du dépôt, pas une chaîne recopiée ici.
/// Recopier la valeur attendue à côté du fichier ne prouverait que ma capacité
/// à recopier.
/// </remarks>
public class ConfigurationLivreeTests
{
    private static readonly DateTime Attendue = new(2026, 9, 1);

    private static string Racine()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "SageFne.sln")))
        {
            dossier = dossier.Parent;
        }

        Assert.NotNull(dossier);
        return dossier!.FullName;
    }

    private static FneOptions Lire(string projet)
    {
        var fichier = Path.Combine(Racine(), "src", projet, "appsettings.json");
        Assert.True(File.Exists(fichier), $"{fichier} est introuvable.");

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(fichier, optional: false)
            .Build();

        var options = new FneOptions();
        configuration.GetSection(FneOptions.Section).Bind(options);
        return options;
    }

    [Theory]
    [InlineData("SageFne.Reader")]
    [InlineData("SageFne.Agent")]
    public void La_date_de_demarrage_livree_se_lie_bien(string projet)
    {
        var options = Lire(projet);

        Assert.NotNull(options.DemarrageLe);
        Assert.Equal(Attendue, options.DemarrageLe!.Value.Date);
    }

    [Fact]
    public void Le_CLI_et_l_agent_partagent_la_meme_frontiere()
    {
        // Deux dates pour une seule frontière : le CLI dirait qu'une pièce peut
        // partir et l'agent l'écarterait, ou l'inverse. Le genre de désaccord
        // qu'on ne découvre qu'en cherchant pourquoi une facture n'est jamais
        // partie.
        Assert.Equal(Lire("SageFne.Reader").DemarrageLe, Lire("SageFne.Agent").DemarrageLe);
    }
}
