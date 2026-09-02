using SageFne.Agent.Surveillance;
using SageFne.Core.Batch;
using SageFne.Core.Certification;
using SageFne.Core.Fne;
using SageFne.Core.Models.Fne;
using SageFne.Core.Models.Sage;
using SageFne.Core.Validation;

namespace SageFne.Agent.Tests;

/// <summary>
/// Ce que l'agent décide, pièce par pièce.
/// </summary>
/// <remarks>
/// Le moteur ne porte aucune règle fiscale : il lit l'état que le lecteur a
/// établi et y ajoute la stabilité et le mode. Ces tests vérifient donc l'ordre
/// des questions, et surtout qu'aucune ne se saute.
/// </remarks>
public class MoteurSurveillanceTests
{
    private static readonly DateTimeOffset Depart = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);

    private static InvoiceConversion Piece(
        EtatPiece etat, string empreinte = "abc", string piece = "1221",
        EtatFne? auRegistre = null, params Constat[] constats)
    {
        var rapport = new CheckReport();
        foreach (var constat in constats)
        {
            if (constat.Severite == Severite.Erreur) rapport.Erreur(constat.Code, constat.Message);
            else rapport.Avertir(constat.Code, constat.Message);
        }

        return new InvoiceConversion
        {
            Header = new SageDocumentHeader
            {
                Piece = piece,
                Domaine = 0,
                Type = 6,
                DocType = 6,
                Date = new DateTime(2026, 9, 2),
                Tiers = "4111GEMSCI",
            },
            Lines = [],
            Report = rapport,
            Etat = etat,
            Empreinte = empreinte,
            Invoice = etat == EtatPiece.ACertifier ? new FneInvoice() : null,
            Certification = auRegistre is { } inscrit
                ? new CertifiedInvoice
                {
                    Identite = "0/6/" + piece,
                    Piece = piece,
                    Etat = inscrit,
                    Empreinte = empreinte,
                }
                : null,
        };
    }

    private static (MoteurSurveillance, HorlogeReglable) Moteur(
        ModeAgent mode = ModeAgent.Automatic, int stabiliteMinutes = 5)
    {
        var horloge = new HorlogeReglable(Depart);
        var stabilite = new VerificateurStabilite(TimeSpan.FromMinutes(stabiliteMinutes), horloge);

        // Le lecteur n'est pas sollicité par Decider() : ces tests portent sur
        // la décision, pas sur la lecture, qui a ses propres tests côté Core.
        return (new MoteurSurveillance(null!, stabilite, mode), horloge);
    }

    // --- Détection et stabilité ---------------------------------------------

    [Fact]
    public void Une_facture_de_type_6_est_detectee_sans_attendre_la_comptabilisation()
    {
        var (moteur, horloge) = Moteur();
        var facture = Piece(EtatPiece.ACertifier);

        // Premier passage : vue, pas encore stable.
        Assert.Equal(MotifAttente.JamaisVue, moteur.Decider(facture).Motif);

        horloge.Avancer(TimeSpan.FromMinutes(6));

        Assert.True(moteur.Decider(facture).Envoyable);
    }

    [Fact]
    public void Une_piece_instable_ne_part_pas()
    {
        var (moteur, horloge) = Moteur();

        moteur.Decider(Piece(EtatPiece.ACertifier, empreinte: "version-1"));
        horloge.Avancer(TimeSpan.FromMinutes(6));

        // La saisie a continué : l'empreinte a changé.
        var decision = moteur.Decider(Piece(EtatPiece.ACertifier, empreinte: "version-2"));

        Assert.Equal(MotifAttente.ContenuInstable, decision.Motif);
        Assert.False(decision.Envoyable);
    }

    [Fact]
    public void Une_piece_stable_et_conforme_est_envoyable()
    {
        var (moteur, horloge) = Moteur();
        var facture = Piece(EtatPiece.ACertifier);

        moteur.Decider(facture);
        horloge.Avancer(TimeSpan.FromMinutes(6));

        var decision = moteur.Decider(facture);

        Assert.True(decision.Envoyable);
        Assert.Equal(MotifAttente.Aucun, decision.Motif);
    }

    // --- Ce que le registre a déjà tranché -----------------------------------

    [Fact]
    public void Le_passage_de_6_a_7_ne_declenche_aucun_second_envoi()
    {
        // L'identité ne change pas à la comptabilisation : domaine/DocType/Piece
        // reste 0/6/1221. Le registre reconnaît donc la même pièce, et le
        // lecteur la classe « déjà certifiée » — quel que soit le DO_Type.
        var (moteur, _) = Moteur();

        var apresComptabilisation = Piece(EtatPiece.DejaCertifiee, auRegistre: EtatFne.Certified);

        var decision = moteur.Decider(apresComptabilisation);

        Assert.False(decision.Envoyable);
        Assert.Equal(MotifAttente.DejaTraitee, decision.Motif);
        Assert.Equal("0/6/1221", decision.Identite);
    }

    [Fact]
    public void Une_certifiee_modifiee_depuis_est_bloquee_sans_second_post()
    {
        var (moteur, _) = Moteur();

        var decision = moteur.Decider(
            Piece(EtatPiece.ModifieeDepuis, auRegistre: EtatFne.Certified));

        Assert.False(decision.Envoyable);
        Assert.Contains("DOCUMENT_MODIFIE_APRES_CERTIFICATION", decision.Explication);
        Assert.Contains("avoir", decision.Explication);
    }

    [Fact]
    public void Un_envoi_en_suspens_n_est_jamais_retente()
    {
        // Le cas du 500. La pièce reste en Sending, et l'agent ne la repropose
        // pas : c'est exactement ainsi que le doublon de la 1072 s'est
        // fabriqué, un renvoi après une réponse qu'on croyait négative.
        var (moteur, horloge) = Moteur();
        var apres500 = Piece(EtatPiece.EnSuspens, auRegistre: EtatFne.Sending);

        // « Non envoyable » ne suffit pas à l'affirmer : une pièce écartée pour
        // n'importe quel autre motif le serait aussi. C'est le registre qui doit
        // la retenir, et le motif le dit.
        Assert.Equal(MotifAttente.DejaTraitee, moteur.Decider(apres500).Motif);

        horloge.Avancer(TimeSpan.FromDays(1));

        Assert.Equal(MotifAttente.DejaTraitee, moteur.Decider(apres500).Motif);
    }

    [Fact]
    public void Une_piece_deposee_au_portail_n_est_jamais_retentee()
    {
        var (moteur, _) = Moteur();

        var decision = moteur.Decider(Piece(EtatPiece.Transmise, auRegistre: EtatFne.Transmise));

        Assert.False(decision.Envoyable);
        Assert.Equal(MotifAttente.DejaTraitee, decision.Motif);
    }

    // --- Conformité ----------------------------------------------------------

    [Fact]
    public void Une_piece_non_conforme_est_bloquee_et_nomme_ses_causes()
    {
        var (moteur, _) = Moteur();

        var decision = moteur.Decider(Piece(
            EtatPiece.Bloquee,
            constats: new Constat(Severite.Erreur, "CLIENT_SANS_NCC", "NCC absent")));

        Assert.False(decision.Envoyable);
        Assert.Equal(MotifAttente.NonConforme, decision.Motif);
        Assert.Contains("CLIENT_SANS_NCC", decision.Explication);
    }

    [Fact]
    public void Une_piece_non_conforme_n_est_meme_pas_observee()
    {
        // Inutile d'attendre la stabilité d'une pièce que rien ne laissera
        // partir : ce serait promettre un envoi après cinq minutes, qui
        // n'arrivera jamais.
        var (moteur, horloge) = Moteur();
        var bloquee = Piece(EtatPiece.Bloquee);

        moteur.Decider(bloquee);
        horloge.Avancer(TimeSpan.FromMinutes(6));

        Assert.Equal(MotifAttente.NonConforme, moteur.Decider(bloquee).Motif);
    }

    // --- Les trois modes -----------------------------------------------------

    [Theory]
    [InlineData(ModeAgent.Manual)]
    [InlineData(ModeAgent.SemiAutomatic)]
    public void Hors_du_mode_automatique_rien_ne_part(ModeAgent mode)
    {
        var (moteur, horloge) = Moteur(mode);
        var facture = Piece(EtatPiece.ACertifier);

        moteur.Decider(facture);
        horloge.Avancer(TimeSpan.FromMinutes(6));

        var decision = moteur.Decider(facture);

        Assert.False(decision.Envoyable);
        Assert.Equal(MotifAttente.ModeNonAutomatique, decision.Motif);
    }

    [Fact]
    public void Le_mode_ne_change_jamais_la_conformite()
    {
        // Automatic n'assouplit rien : une pièce bloquée l'est dans les trois
        // modes, et pour la même raison.
        foreach (var mode in Enum.GetValues<ModeAgent>())
        {
            var (moteur, _) = Moteur(mode);
            Assert.Equal(MotifAttente.NonConforme, moteur.Decider(Piece(EtatPiece.Bloquee)).Motif);
        }
    }

    [Fact]
    public void Le_mode_par_defaut_n_envoie_rien()
    {
        // Un paramétrage absent, mal orthographié ou oublié doit retomber sur
        // le mode qui ne certifie rien.
        Assert.Equal(ModeAgent.Manual, new SageFne.Agent.Configuration.AgentOptions().Mode);
        Assert.Equal(ModeAgent.Manual, (ModeAgent)0);
    }

    [Fact]
    public void Une_piece_hors_perimetre_n_est_pas_annoncee_bloquee()
    {
        // Le motif porte la nuance, et il la porte parce que le journal la
        // perdrait sinon : une facture de 2024 écartée par décision serait
        // annoncée « bloquée », et l'on chercherait le NCC manquant d'une pièce
        // que personne n'a l'intention d'envoyer.
        var (moteur, _) = Moteur(ModeAgent.Automatic);

        var decision = moteur.Decider(Piece(EtatPiece.HorsPerimetre));

        Assert.Equal(MotifAttente.HorsPerimetre, decision.Motif);
        Assert.NotEqual(MotifAttente.NonConforme, decision.Motif);
        Assert.False(decision.Envoyable);
        Assert.DoesNotContain("bloquée", decision.Explication);
    }

    [Fact]
    public void Le_perimetre_l_emporte_meme_en_mode_automatique()
    {
        // Rien ne doit pouvoir faire partir une pièce antérieure au démarrage :
        // ni le mode, ni la stabilité, ni un second passage.
        var (moteur, _) = Moteur(ModeAgent.Automatic);
        var piece = Piece(EtatPiece.HorsPerimetre);

        moteur.Decider(piece);

        Assert.False(moteur.Decider(piece).Envoyable);
    }
}
