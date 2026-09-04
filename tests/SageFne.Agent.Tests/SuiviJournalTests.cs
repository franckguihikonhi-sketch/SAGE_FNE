using SageFne.Agent.Surveillance;

namespace SageFne.Agent.Tests;

/// <summary>
/// Le journal ne réécrit que ce qui a changé.
/// </summary>
/// <remarks>
/// Une ligne par pièce et par tour : sur quatorze pièces déjà traitées, cela
/// faisait vingt mille lignes par jour pour des pièces qui ne bougeront plus.
/// Un journal qu'on ne peut plus lire est un journal qui ne sert plus, et c'est
/// là qu'on cherche quand quelque chose ne va pas.
/// </remarks>
public class SuiviJournalTests
{
    private static DecisionAgent Decision(
        string piece, MotifAttente motif, string explication = "") =>
        new(piece, $"0/6/{piece}", motif, explication == "" ? $"Pièce {piece} : {motif}." : explication);

    [Fact]
    public void Une_piece_inchangee_ne_se_reecrit_pas()
    {
        var journal = new SuiviJournal();
        var decision = Decision("1230", MotifAttente.DejaTraitee);

        Assert.True(journal.AEcrire(decision));
        Assert.False(journal.AEcrire(decision));
        Assert.False(journal.AEcrire(decision));
    }

    [Fact]
    public void Un_changement_d_etat_se_reecrit()
    {
        var journal = new SuiviJournal();

        Assert.True(journal.AEcrire(Decision("1223", MotifAttente.NonConforme)));
        Assert.True(journal.AEcrire(Decision("1223", MotifAttente.Aucun)));
    }

    [Fact]
    public void Le_compte_a_rebours_de_stabilite_continue_de_s_ecrire()
    {
        // Le motif seul aurait confondu « il reste 240 s » et « il reste
        // 180 s » : l'exploitant n'aurait plus vu la pièce avancer, et aurait
        // cru l'agent bloqué. C'est l'explication entière qui fait foi.
        var journal = new SuiviJournal();

        Assert.True(journal.AEcrire(
            Decision("1235", MotifAttente.DelaiNonEcoule, "il reste 240 s")));
        Assert.True(journal.AEcrire(
            Decision("1235", MotifAttente.DelaiNonEcoule, "il reste 180 s")));
    }

    [Fact]
    public void Deux_pieces_se_suivent_separement()
    {
        var journal = new SuiviJournal();

        Assert.True(journal.AEcrire(Decision("1230", MotifAttente.DejaTraitee)));
        Assert.True(journal.AEcrire(Decision("1231", MotifAttente.DejaTraitee)));
        Assert.False(journal.AEcrire(Decision("1230", MotifAttente.DejaTraitee)));
        Assert.Equal(2, journal.Suivies);
    }

    [Fact]
    public void Une_piece_oubliee_se_reecrit()
    {
        // Après un envoi réussi : la pièce va changer d'état, et son prochain
        // passage au journal doit se voir.
        var journal = new SuiviJournal();
        var decision = Decision("1230", MotifAttente.DejaTraitee);

        Assert.True(journal.AEcrire(decision));
        journal.Oublier(decision.Identite);
        Assert.True(journal.AEcrire(decision));
    }

    [Fact]
    public void La_synthese_dit_ce_que_le_detail_retenu_aurait_dit()
    {
        // Elle est écrite à chaque tour, elle : c'est elle qui prouve que
        // l'agent tourne encore. Un journal muet ne se distingue pas d'un
        // service arrêté.
        var synthese = SuiviJournal.Synthese([
            Decision("1225", MotifAttente.DejaTraitee),
            Decision("1226", MotifAttente.DejaTraitee),
            Decision("1223", MotifAttente.NonConforme),
            Decision("1235", MotifAttente.Aucun),
        ]);

        Assert.Contains("4 pièce(s)", synthese);
        Assert.Contains("2 déjà traitée", synthese);
        Assert.Contains("1 bloquée par un contrôle", synthese);
        Assert.Contains("1 prête à partir", synthese);
    }

    [Fact]
    public void Une_fenetre_vide_le_dit_plutot_que_de_se_taire()
    {
        Assert.Contains("Aucune pièce", SuiviJournal.Synthese([]));
    }
}
