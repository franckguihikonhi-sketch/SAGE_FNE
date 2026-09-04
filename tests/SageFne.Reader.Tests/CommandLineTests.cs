using SageFne.Core.Batch;
using SageFne.Reader.Batch;

namespace SageFne.Core.Tests;

public class CommandLineTests
{
    [Fact]
    public void Sans_argument_le_lot_n_est_pas_filtre()
    {
        var ligne = CommandLine.Parse([]);

        Assert.Empty(ligne.Query.Pieces);
        Assert.Null(ligne.Query.Depuis);
        Assert.Equal(500, ligne.Query.Limite);
        Assert.Empty(ligne.Erreurs);
    }

    [Fact]
    public void Les_pieces_se_donnent_les_unes_apres_les_autres()
    {
        var ligne = CommandLine.Parse(["1219", "1220", "1221"]);

        Assert.Equal(["1219", "1220", "1221"], ligne.Query.Pieces);
    }

    [Fact]
    public void La_borne_haute_saisie_est_comprise_dans_le_lot()
    {
        var ligne = CommandLine.Parse(["--du", "2025-12-01", "--au", "2025-12-31"]);

        // « jusqu'au 31 » doit inclure les pièces datées du 31 à 23 h : la
        // requête reçoit donc le 1er janvier, exclu.
        Assert.Equal(new DateTime(2025, 12, 1), ligne.Query.Depuis);
        Assert.Equal(new DateTime(2026, 1, 1), ligne.Query.Jusqua);
    }

    [Fact]
    public void Une_date_illisible_est_refusee_avec_un_message()
    {
        var ligne = CommandLine.Parse(["--du", "le mois dernier"]);

        Assert.Single(ligne.Erreurs);
        Assert.Contains("--du", ligne.Erreurs[0]);
    }

    [Fact]
    public void Une_option_inconnue_est_refusee()
    {
        var ligne = CommandLine.Parse(["--nimporte"]);

        Assert.Contains(ligne.Erreurs, erreur => erreur.Contains("--nimporte"));
    }

    [Fact]
    public void La_sortie_et_le_json_se_demandent_explicitement()
    {
        var ligne = CommandLine.Parse(["--json", "--sortie", "sorties", "--limite", "20"]);

        Assert.True(ligne.AfficherJson);
        Assert.Equal("sorties", ligne.Sortie);
        Assert.Equal(20, ligne.Query.Limite);
    }

    [Theory]
    [InlineData("debloquer")]
    [InlineData("débloquer")]
    public void Le_deblocage_se_lit_avec_ou_sans_accent(string verbe)
    {
        var ligne = CommandLine.Parse([verbe, "1052", "--non-certifiee", "--confirmer"]);

        Assert.Equal(Verbe.Debloquer, ligne.Verbe);
        Assert.Equal(["1052"], ligne.Query.Pieces);
        Assert.True(ligne.NonCertifiee);
        Assert.True(ligne.Confirme);
        Assert.Null(ligne.Reference);
    }

    [Fact]
    public void La_reference_du_portail_se_lit()
    {
        var ligne = CommandLine.Parse(["debloquer", "1052", "--reference", "REF-1", "--confirmer"]);

        Assert.Equal("REF-1", ligne.Reference);
        Assert.False(ligne.NonCertifiee);
    }

    [Fact]
    public void Une_reference_vide_est_refusee()
    {
        var ligne = CommandLine.Parse(["debloquer", "1052", "--reference"]);

        Assert.Contains(ligne.Erreurs, erreur => erreur.Contains("portail"));
    }

    [Theory]
    [InlineData("statut")]
    [InlineData("status")]
    public void Le_statut_se_lit(string verbe)
    {
        var ligne = CommandLine.Parse([verbe, "1052"]);

        Assert.Equal(Verbe.Statut, ligne.Verbe);
        Assert.Equal(["1052"], ligne.Query.Pieces);
        Assert.False(ligne.Confirme);
    }

    [Fact]
    public void Le_registre_info_se_lit()
    {
        var ligne = CommandLine.Parse(["registre-info"]);

        Assert.Equal(Verbe.RegistreInfo, ligne.Verbe);
        Assert.Empty(ligne.Erreurs);
    }

    [Theory]
    [InlineData("reconcilier")]
    [InlineData("réconcilier")]
    public void La_reconciliation_se_lit_avec_ou_sans_accent(string verbe)
    {
        var ligne = CommandLine.Parse(
            [verbe, "1052", "--reference", "2304903U26000000930", "--token", "QR", "--confirmer"]);

        Assert.Equal(Verbe.Reconcilier, ligne.Verbe);
        Assert.Equal(["1052"], ligne.Query.Pieces);
        Assert.Equal("2304903U26000000930", ligne.Reference);
        Assert.Equal("QR", ligne.Jeton);
        Assert.True(ligne.Confirme);
    }

    [Fact]
    public void Un_jeton_vide_est_refuse()
    {
        var ligne = CommandLine.Parse(["reconcilier", "1052", "--token"]);

        Assert.Contains(ligne.Erreurs, erreur => erreur.Contains("portail"));
    }

    [Fact]
    public void Sans_verbe_de_deblocage_rien_n_est_demande()
    {
        var ligne = CommandLine.Parse(["1052"]);

        Assert.False(ligne.NonCertifiee);
        Assert.Null(ligne.Reference);
    }
}
