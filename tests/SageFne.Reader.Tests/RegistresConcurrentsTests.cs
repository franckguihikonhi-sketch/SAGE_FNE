using SageFne.Core.Configuration;
using SageFne.Reader.Batch;

namespace SageFne.Core.Tests;

/// <summary>
/// Deux registres sur une machine, c'est deux mémoires partielles d'une chose
/// qui n'en admet qu'une.
/// </summary>
/// <remarks>
/// Le cas s'est présenté en exploitation : le service écrivait sous
/// C:\ProgramData, le CLI sous %APPDATA%, et « registre-info » affichait quatre
/// certifications là où il y en avait dix. Un envoi lancé depuis ce compte
/// aurait renvoyé à la DGI une facture déjà certifiée.
/// </remarks>
public class RegistresConcurrentsTests
{
    private static readonly string Machine = Path.GetFullPath(ServicesMiddleware.CheminMachine());
    private static readonly string Profil = Path.GetFullPath(ServicesMiddleware.CheminDurable());
    private const string Bin = "/opt/agent";

    private static Func<string, bool> Presents(params string[] chemins)
    {
        var jeu = new HashSet<string>(chemins.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        return jeu.Contains;
    }

    [Fact]
    public void Le_registre_du_service_se_voit_depuis_celui_du_profil()
    {
        var ailleurs = ServicesMiddleware.RegistresConcurrents(Profil, Bin, Presents(Machine, Profil));

        var seul = Assert.Single(ailleurs);
        Assert.Equal(Machine, seul.Chemin);
    }

    [Fact]
    public void Le_registre_en_usage_ne_se_denonce_pas_lui_meme()
    {
        // Seul le fichier du profil existe : il n'y a pas de conflit, et rien
        // ne doit être signalé — sans quoi l'avertissement devient du bruit
        // qu'on apprend à ignorer.
        Assert.Empty(ServicesMiddleware.RegistresConcurrents(Profil, Bin, Presents(Profil)));
    }

    [Fact]
    public void Un_registre_absent_n_est_pas_signale()
    {
        Assert.Empty(ServicesMiddleware.RegistresConcurrents(Profil, Bin, _ => false));
    }

    [Fact]
    public void L_ancien_emplacement_reste_signale()
    {
        var ancien = Path.GetFullPath(ServicesMiddleware.AncienChemin(Bin));

        var ailleurs = ServicesMiddleware.RegistresConcurrents(Machine, Bin, Presents(Machine, ancien));

        Assert.Equal(ancien, Assert.Single(ailleurs).Chemin);
    }

    [Fact]
    public void Chaque_registre_dit_ce_qu_il_est()
    {
        // L'exploitant doit pouvoir choisir : un chemin sans explication ne se
        // départage pas.
        var ailleurs = ServicesMiddleware.RegistresConcurrents(Profil, Bin, Presents(Machine, Profil));

        Assert.All(ailleurs, autre => Assert.False(string.IsNullOrWhiteSpace(autre.Pourquoi)));
    }

    [Fact]
    public void Le_registre_du_service_vient_en_premier()
    {
        // C'est celui que la commande propose en exemple : le service certifie
        // en continu, son registre est le plus complet des deux.
        var ancien = Path.GetFullPath(ServicesMiddleware.AncienChemin(Bin));

        var ailleurs = ServicesMiddleware.RegistresConcurrents(Profil, Bin, Presents(Machine, ancien, Profil));

        Assert.Equal(Machine, ailleurs[0].Chemin);
    }

    // --- Quels verbes sont bloqués -----------------------------------------

    [Theory]
    [InlineData(Verbe.Envoyer)]
    [InlineData(Verbe.Debloquer)]
    [InlineData(Verbe.Reconcilier)]
    [InlineData(Verbe.CorrigerReconciliation)]
    [InlineData(Verbe.ReparerSource)]
    public void Les_verbes_qui_inscrivent_sont_reconnus(Verbe verbe) =>
        Assert.True(Verbes.EcritAuRegistre(verbe));

    [Theory]
    [InlineData(Verbe.DryRun)]
    [InlineData(Verbe.Statut)]
    [InlineData(Verbe.RegistreInfo)]
    [InlineData(Verbe.Detail)]
    [InlineData(Verbe.Candidats)]
    [InlineData(Verbe.Domaines)]
    [InlineData(Verbe.Verification)]
    [InlineData(Verbe.Ncc)]
    public void Les_verbes_de_lecture_ne_sont_pas_bloques(Verbe verbe) =>
        Assert.False(Verbes.EcritAuRegistre(verbe));
}
