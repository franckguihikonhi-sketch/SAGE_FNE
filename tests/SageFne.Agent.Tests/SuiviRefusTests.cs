using SageFne.Agent.Surveillance;

namespace SageFne.Agent.Tests;

/// <summary>
/// Quand réessayer une pièce que la plateforme a refusée.
/// </summary>
/// <remarks>
/// Deux fautes se font face, et il a fallu les deux pour trouver le milieu.
///
/// Réessayer à chaque tour : la pièce 1225 est repartie une fois par minute
/// entre 13:28 et 13:32, martelant la plateforme.
///
/// Ne jamais réessayer un contenu inchangé : ma correction, fondée sur la
/// supposition qu'un 400 soit déterministe. La même 1225, corps identique, est
/// passée en 201 à 13:42. Le refus était passager, et sans réessai la pièce
/// serait restée bloquée jusqu'à ce qu'un humain s'en aperçoive.
/// </remarks>
public class SuiviRefusTests
{
    private static readonly DateTimeOffset Depart = new(2026, 9, 2, 13, 28, 0, TimeSpan.Zero);

    private static (SuiviRefus Suivi, HorlogeReglable Horloge) Monter()
    {
        var horloge = new HorlogeReglable(Depart);
        return (new SuiviRefus(horloge), horloge);
    }

    [Fact]
    public void Le_premier_refus_impose_une_attente()
    {
        var (suivi, _) = Monter();

        var decision = suivi.Constater("0/6/1225", "corps", Depart);

        Assert.False(decision.PeutRepartir);
        Assert.Equal(1, decision.Tentatives);
        Assert.Equal(TimeSpan.FromMinutes(5), decision.Reste);
    }

    [Fact]
    public void Un_tour_de_lecture_n_epuise_pas_les_tentatives()
    {
        // Le compteur n'avance qu'au constat d'un NOUVEAU refus, prouvé par
        // l'horodatage du registre. Sinon une lecture par minute épuiserait les
        // cinq tentatives en cinq minutes, sans qu'aucun envoi n'ait eu lieu.
        var (suivi, horloge) = Monter();

        for (var minute = 0; minute < 4; minute++)
        {
            suivi.Constater("0/6/1225", "corps", Depart);
            horloge.Avancer(TimeSpan.FromMinutes(1));
        }

        var decision = suivi.Constater("0/6/1225", "corps", Depart);

        Assert.Equal(1, decision.Tentatives);
    }

    [Fact]
    public void Apres_l_attente_la_piece_repart()
    {
        // Le cas de la 1225 : refusée à 13:28, repassée en 201 à 13:42. Avec
        // cinq minutes d'attente, l'agent l'aurait retentée seul à 13:33.
        var (suivi, horloge) = Monter();
        suivi.Constater("0/6/1225", "corps", Depart);

        horloge.Avancer(TimeSpan.FromMinutes(5));
        var decision = suivi.Constater("0/6/1225", "corps", Depart);

        Assert.True(decision.PeutRepartir);
        Assert.Equal(TimeSpan.Zero, decision.Reste);
    }

    [Fact]
    public void L_attente_grandit_a_chaque_refus()
    {
        var (suivi, horloge) = Monter();
        var refuseLe = Depart;

        var attendus = new[] { 5d, 15d, 45d, 120d };
        foreach (var attendu in attendus)
        {
            var decision = suivi.Constater("0/6/1225", "corps", refuseLe);
            Assert.Equal(TimeSpan.FromMinutes(attendu), decision.Reste);

            // Le refus suivant, plus tard : c'est lui qui fait avancer le compteur.
            horloge.Avancer(TimeSpan.FromMinutes(attendu));
            refuseLe = horloge.GetUtcNow();
        }
    }

    [Fact]
    public void Au_cinquieme_refus_l_agent_cesse_de_reessayer()
    {
        // Un refus qui se répète cinq fois sur le même corps n'est plus
        // passager. Continuer serait marteler ; se taire serait cacher.
        var (suivi, horloge) = Monter();
        var refuseLe = Depart;
        DecisionRefus decision = default;

        for (var essai = 0; essai < SuiviRefus.TentativesMaximum; essai++)
        {
            decision = suivi.Constater("0/6/1225", "corps", refuseLe);
            horloge.Avancer(TimeSpan.FromHours(3));
            refuseLe = horloge.GetUtcNow();
        }

        Assert.False(decision.PeutRepartir);
        Assert.Equal(SuiviRefus.TentativesMaximum, decision.Tentatives);
        Assert.Null(decision.Reste);
    }

    [Fact]
    public void Un_contenu_corrige_repart_de_zero()
    {
        // La correction dans Sage efface l'histoire des refus : ils portaient
        // sur un corps qui n'existe plus.
        var (suivi, horloge) = Monter();
        var refuseLe = Depart;

        for (var essai = 0; essai < SuiviRefus.TentativesMaximum; essai++)
        {
            suivi.Constater("0/6/1225", "corps-refuse", refuseLe);
            horloge.Avancer(TimeSpan.FromHours(3));
            refuseLe = horloge.GetUtcNow();
        }

        var decision = suivi.Constater("0/6/1225", "corps-corrige", refuseLe);

        Assert.Equal(1, decision.Tentatives);
        Assert.Equal(TimeSpan.FromMinutes(5), decision.Reste);
    }

    [Fact]
    public void Une_piece_reglee_est_oubliee()
    {
        var (suivi, _) = Monter();
        suivi.Constater("0/6/1225", "corps", Depart);

        suivi.Oublier("0/6/1225");

        Assert.Equal(0, suivi.EnAttente);
    }
}
