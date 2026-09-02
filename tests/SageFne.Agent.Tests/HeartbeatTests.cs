using SageFne.Agent.Sante;

namespace SageFne.Agent.Tests;

/// <summary>
/// Ce que le battement dit, et surtout ce qu'il ne doit pas laisser deviner.
/// </summary>
public class HeartbeatTests
{
    private static Heartbeat Battement(string dossier) => new(
        AgentId: "POSTE-1",
        CompanyId: dossier,
        Version: "1.0.0.0",
        Quand: DateTimeOffset.UnixEpoch,
        Sage: EtatLien.Disponible,
        Reseau: EtatLien.Indisponible,
        Environnement: "TEST",
        Mode: "Manual");

    [Fact]
    public void Un_dossier_non_renseigne_se_voit_comme_tel()
    {
        // « dossier= » suivi de rien se lit comme un dossier nommé par la chaîne
        // vide. Sur le premier poste, le battement affichait « dossier= » et
        // rien n'indiquait s'il s'agissait d'un paramétrage manquant ou d'un
        // dossier réellement anonyme.
        var ligne = Battement("").ToString();

        Assert.Contains("dossier=(non renseigné)", ligne);
        Assert.DoesNotContain("dossier= ", ligne);
    }

    [Fact]
    public void Un_dossier_fait_d_espaces_ne_vaut_pas_mieux_qu_un_dossier_vide()
    {
        Assert.Contains("dossier=(non renseigné)", Battement("   ").ToString());
    }

    [Fact]
    public void Un_dossier_renseigne_est_repris_tel_quel()
    {
        Assert.Contains("dossier=GEMS-CI", Battement("GEMS-CI").ToString());
    }

    [Fact]
    public void Le_battement_ne_porte_ni_cle_ni_adresse_ni_client()
    {
        // Ce battement finira dans un fichier, un Event Log, puis une télémétrie
        // SaaS. Ce qui n'y entre pas n'en fuitera pas.
        var ligne = Battement("GEMS-CI").ToString();

        Assert.DoesNotContain("http", ligne, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", ligne, StringComparison.OrdinalIgnoreCase);
    }
}
