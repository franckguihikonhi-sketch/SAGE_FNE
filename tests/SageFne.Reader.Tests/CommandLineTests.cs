using SageFne.Reader.Batch;

namespace SageFne.Reader.Tests;

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
}
