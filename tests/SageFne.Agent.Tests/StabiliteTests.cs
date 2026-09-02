using SageFne.Agent.Surveillance;

namespace SageFne.Agent.Tests;

/// <summary>Une horloge qu'on avance à la main.</summary>
internal sealed class HorlogeReglable(DateTimeOffset depart) : TimeProvider
{
    private DateTimeOffset _maintenant = depart;

    public override DateTimeOffset GetUtcNow() => _maintenant;

    public void Avancer(TimeSpan duree) => _maintenant += duree;
}

/// <summary>
/// La vérification de stabilité : deux lectures identiques, séparées d'un délai.
/// </summary>
/// <remarks>
/// Une facture apparaît dans Sage dès la première ligne saisie. Certifier à cet
/// instant certifierait un brouillon — et une facture certifiée ne s'annule pas.
/// Ce que ces tests protègent, c'est la patience.
/// </remarks>
public class StabiliteTests
{
    private static readonly DateTimeOffset Depart = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Delai = TimeSpan.FromMinutes(5);

    private static (VerificateurStabilite, HorlogeReglable) Verificateur()
    {
        var horloge = new HorlogeReglable(Depart);
        return (new VerificateurStabilite(Delai, horloge), horloge);
    }

    [Fact]
    public void Une_piece_vue_pour_la_premiere_fois_n_est_jamais_stable()
    {
        var (verificateur, _) = Verificateur();

        Assert.Equal(MotifAttente.JamaisVue, verificateur.Constater("0/6/1221", "abc"));
    }

    [Fact]
    public void Revue_trop_tot_elle_attend_encore()
    {
        var (verificateur, horloge) = Verificateur();
        verificateur.Constater("0/6/1221", "abc");

        horloge.Avancer(TimeSpan.FromMinutes(4));

        Assert.Equal(MotifAttente.DelaiNonEcoule, verificateur.Constater("0/6/1221", "abc"));
    }

    [Fact]
    public void Deux_lectures_identiques_apres_le_delai_la_rendent_stable()
    {
        var (verificateur, horloge) = Verificateur();
        verificateur.Constater("0/6/1221", "abc");

        horloge.Avancer(TimeSpan.FromMinutes(6));

        Assert.Equal(MotifAttente.Aucun, verificateur.Constater("0/6/1221", "abc"));
    }

    [Fact]
    public void Un_contenu_qui_change_remet_le_compteur_a_zero()
    {
        // Le cas qui compte : la saisie continue. Sans la remise à zéro, une
        // facture modifiée une seconde avant l'échéance partirait sur la foi
        // d'un délai écoulé pour une version qui n'existe plus.
        var (verificateur, horloge) = Verificateur();
        verificateur.Constater("0/6/1221", "abc");

        horloge.Avancer(TimeSpan.FromMinutes(4));
        Assert.Equal(MotifAttente.ContenuInstable, verificateur.Constater("0/6/1221", "def"));

        horloge.Avancer(TimeSpan.FromMinutes(2));
        Assert.Equal(MotifAttente.DelaiNonEcoule, verificateur.Constater("0/6/1221", "def"));

        horloge.Avancer(TimeSpan.FromMinutes(4));
        Assert.Equal(MotifAttente.Aucun, verificateur.Constater("0/6/1221", "def"));
    }

    [Fact]
    public void Une_piece_sans_empreinte_n_est_pas_declaree_stable()
    {
        // Une pièce qui ne se traduit pas n'a pas d'empreinte. Deux absences ne
        // font pas une égalité : la traiter comme « inchangée » l'aurait rendue
        // stable, donc envoyable, alors qu'elle est bloquée.
        var (verificateur, horloge) = Verificateur();

        Assert.Equal(MotifAttente.NonConforme, verificateur.Constater("0/6/1221", ""));
        horloge.Avancer(TimeSpan.FromHours(1));
        Assert.Equal(MotifAttente.NonConforme, verificateur.Constater("0/6/1221", ""));
    }

    [Fact]
    public void Deux_pieces_se_suivent_separement()
    {
        var (verificateur, horloge) = Verificateur();
        verificateur.Constater("0/6/1221", "abc");
        horloge.Avancer(TimeSpan.FromMinutes(6));
        verificateur.Constater("0/6/1222", "xyz");

        Assert.Equal(MotifAttente.Aucun, verificateur.Constater("0/6/1221", "abc"));
        Assert.Equal(MotifAttente.DelaiNonEcoule, verificateur.Constater("0/6/1222", "xyz"));
    }

    [Fact]
    public void Une_piece_oubliee_repart_de_zero()
    {
        var (verificateur, horloge) = Verificateur();
        verificateur.Constater("0/6/1221", "abc");
        horloge.Avancer(TimeSpan.FromMinutes(6));

        verificateur.Oublier("0/6/1221");

        Assert.Equal(MotifAttente.JamaisVue, verificateur.Constater("0/6/1221", "abc"));
        Assert.Equal(0, new VerificateurStabilite(Delai).EnObservation);
    }

    [Fact]
    public void Le_redemarrage_ne_fait_que_retarder_un_envoi()
    {
        // Le suivi vit en mémoire. Le perdre ne rend rien envoyable trop tôt :
        // au contraire, la pièce redevient « jamais vue » et attend un tour de
        // plus. L'anti-doublon, lui, ne dépend jamais d'ici.
        var (avant, horloge) = Verificateur();
        avant.Constater("0/6/1221", "abc");
        horloge.Avancer(TimeSpan.FromMinutes(6));

        var apresRedemarrage = new VerificateurStabilite(Delai, horloge);

        Assert.Equal(MotifAttente.JamaisVue, apresRedemarrage.Constater("0/6/1221", "abc"));
    }
}
