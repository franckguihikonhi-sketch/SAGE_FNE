using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SageFne.Agent.Configuration;
using SageFne.Agent.Surveillance;
using SageFne.Core.Batch;
using SageFne.Core.Models.Sage;
using SageFne.Core.Validation;

namespace SageFne.Agent.Tests;

/// <summary>
/// Combien de factures peuvent partir d'un seul tour.
/// </summary>
/// <remarks>
/// Lire deux cents pièces est sans conséquence ; en certifier deux cents en une
/// minute ne se défait pas. Les deux plafonds sont donc distincts, et le second
/// n'existait pas.
///
/// Le risque n'était pas théorique : le dossier porte mille quatre pièces dont
/// la plupart ne sont bloquées que par un NCC ou un téléphone absent dans la
/// fiche client. Le jour où ces fiches sont complétées dans Sage — le travail
/// en cours — un lot entier devient conforme d'un coup, et le premier tour qui
/// suit serait parti avec.
/// </remarks>
public class PlafondEnvoisTests
{
    [Fact]
    public void Le_plafond_d_envois_est_distinct_de_celui_de_lecture()
    {
        // Les confondre reviendrait à n'en avoir aucun : la limite de lecture
        // vaut deux cents, et deux cents certifications d'un coup sont
        // exactement ce que ce plafond existe pour empêcher.
        var reglages = new AgentOptions();

        Assert.NotEqual(reglages.LimiteParTour, reglages.LimiteEnvoisParTour);
        Assert.True(reglages.LimiteEnvoisParTour < reglages.LimiteParTour);
        Assert.True(reglages.LimiteEnvoisParTour > 0);
    }

    [Fact]
    public void Le_defaut_livre_borne_les_envois()
    {
        // Le fichier livré, pas la valeur par défaut du type : c'est lui que
        // l'agent charge sur le poste.
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "SageFne.sln")))
        {
            dossier = dossier.Parent;
        }

        Assert.NotNull(dossier);
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(
                dossier!.FullName, "src", "SageFne.Agent", "appsettings.json"), optional: false)
            .Build();

        var reglages = new AgentOptions();
        configuration.GetSection(AgentOptions.Section).Bind(reglages);

        Assert.InRange(reglages.LimiteEnvoisParTour, 1, 50);
    }

    [Fact]
    public void Le_mode_livre_reste_Manual()
    {
        // Le fichier versionné n'autorise jamais l'envoi de lui-même : le
        // passage en Automatic est un acte d'exploitation, posé en variable
        // machine sur le poste concerné, jamais un défaut qui voyage avec le
        // dépôt.
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "SageFne.sln")))
        {
            dossier = dossier.Parent;
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(
                dossier!.FullName, "src", "SageFne.Agent", "appsettings.json"), optional: false)
            .Build();

        var reglages = new AgentOptions();
        configuration.GetSection(AgentOptions.Section).Bind(reglages);

        Assert.Equal(ModeAgent.Manual, reglages.Mode);
    }

    [Fact]
    public void En_Automatic_une_piece_conforme_et_stable_devient_envoyable()
    {
        // Le pendant du plafond : le mode fait bien ce qu'il annonce. Sans ce
        // test, un plafond mal placé pourrait tout retenir sans que rien ne le
        // dise.
        var stabilite = new VerificateurStabilite(TimeSpan.Zero);
        var moteur = new MoteurSurveillance(null!, stabilite, ModeAgent.Automatic);
        var piece = Conforme();

        moteur.Decider(piece);
        var decision = moteur.Decider(piece);

        Assert.True(decision.Envoyable);
    }

    [Fact]
    public void En_Manual_la_meme_piece_attend_une_decision_humaine()
    {
        var stabilite = new VerificateurStabilite(TimeSpan.Zero);
        var moteur = new MoteurSurveillance(null!, stabilite, ModeAgent.Manual);
        var piece = Conforme();

        moteur.Decider(piece);
        var decision = moteur.Decider(piece);

        Assert.False(decision.Envoyable);
        Assert.Equal(MotifAttente.ModeNonAutomatique, decision.Motif);
    }

    private static InvoiceConversion Conforme() => new()
    {
        Header = new SageDocumentHeader
        {
            Domaine = 0, Type = 6, Piece = "1223",
            Date = DateTime.Today, Tiers = "4111ABAL",
        },
        Lines = [],
        Report = new CheckReport(),
        Empreinte = "stable",
        Etat = EtatPiece.ACertifier,
    };
}
