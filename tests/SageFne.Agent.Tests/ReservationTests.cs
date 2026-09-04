using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SageFne.Core.Batch;
using SageFne.Core.Certification;
using SageFne.Core.Configuration;
using SageFne.Core.Data;
using SageFne.Core.Fne;
using SageFne.Core.Mapping;
using SageFne.Core.Models.Fne;
using SageFne.Core.Saas;

namespace SageFne.Agent.Tests;

/// <summary>
/// La mémoire anti-doublon partagée entre les postes.
/// </summary>
/// <remarks>
/// L'invariant n'est pas « un seul agent ». Deux agents peuvent traiter deux
/// factures différentes du même dossier, et c'est même souhaitable. Ce qui ne
/// doit jamais arriver, c'est que la MÊME pièce parte deux fois.
///
/// On objecte parfois que Sage empêche déjà deux personnes de travailler sur
/// la même facture. C'est vrai de la SAISIE, et c'est ce qui rend l'empreinte
/// fiable. Mais l'agent ne saisit pas : il lit, et un verrou de saisie ne
/// protège pas d'une lecture. La pièce 1225 est partie deux fois — à 13h42
/// puis à 20h43, sous deux références — alors qu'elle était finie depuis
/// longtemps et que personne ne l'éditait. Deux lecteurs, deux registres.
/// </remarks>
public class ReservationTests
{
    private const string Piece = "1221";

    private sealed class ReservationFeinte(SortReservation sort) : IReservationClient
    {
        public bool Actif { get; init; } = true;
        public List<string> Reservees { get; } = [];
        public List<string> Liberees { get; } = [];

        public Task<SortReservation> ReserverAsync(
            string identite, string piece, CancellationToken ct = default)
        {
            if (sort is SortReservation.Obtenue) Reservees.Add(identite);
            return Task.FromResult(sort);
        }

        public Task LibererAsync(string identite, string motif, CancellationToken ct = default)
        {
            Liberees.Add(identite);
            return Task.CompletedTask;
        }
    }

    private sealed class Registre : ICertificationLedger
    {
        public List<CertifiedInvoice> Ecritures { get; } = [];

        public Task<IReadOnlyDictionary<string, CertifiedInvoice>> LookupAsync(
            IReadOnlyCollection<string> identites, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, CertifiedInvoice>>(
                new Dictionary<string, CertifiedInvoice>());

        public Task RecordAsync(CertifiedInvoice certification, CancellationToken ct = default)
        {
            Ecritures.Add(certification);
            return Task.CompletedTask;
        }
    }

    private sealed class ClientTemoin(FneSignResult reponse) : IFneApiClient
    {
        public bool Appele { get; private set; }
        public bool Reel => false;
        public string DecrireRequete(FneInvoice facture) => "POST …";

        public Task<FneSignResult> SignAsync(FneInvoice facture, CancellationToken ct = default)
        {
            Appele = true;
            return Task.FromResult(reponse);
        }
    }

    private static (InvoiceSender Expediteur, ClientTemoin Client, Registre Registre) Monter(
        IReservationClient? reservation, FneSignResult? reponse = null, SuiviAgents? agents = null)
    {
        var registre = new Registre();

        // Les mêmes réglages que le reste des tests d'envoi : identité posée,
        // aucun délai de portail à attendre.
        var reglages = Options.Create(new FneOptions
        {
            PointOfSale = "FISH-AFRIC",
            Establishment = "FISH-AFRIC",
            Template = "B2B",
            PaymentMethod = "deferred",
            PortalCheckDelayMinutes = 0,
        });
        var lecteur = new InvoiceBatchReader(
            new DemoSageInvoiceRepository(estReel: true),
            new FneInvoiceMapper(reglages), registre, reglages);

        var client = new ClientTemoin(
            reponse ?? new FneSignResult(true, 201, "2304903U26000000099"));

        return (new InvoiceSender(
            lecteur, registre, client, NullLogger<InvoiceSender>.Instance, reglages,
            reservation, agents),
            client, registre);
    }

    // --- Ce que la réservation empêche --------------------------------------

    [Fact]
    public async Task Une_piece_deja_partie_ailleurs_ne_repart_pas()
    {
        var (expediteur, client, registre) = Monter(new ReservationFeinte(SortReservation.Refusee));

        var resultat = await expediteur.EnvoyerAsync(Piece, confirme: true);

        Assert.False(resultat.Reussi);
        Assert.False(client.Appele);
        Assert.Empty(registre.Ecritures);
        Assert.Contains("déjà partie", resultat.Message);
    }

    [Fact]
    public async Task Une_base_muette_arrete_un_agent_qui_ignore_s_il_est_seul()
    {
        // Constat « inconnu » : la base n'a jamais répondu à cet agent. Il ne
        // peut donc rien affirmer sur ses semblables, et ne suppose pas.
        var (expediteur, client, _) = Monter(
            new ReservationFeinte(SortReservation.Indisponible), agents: new SuiviAgents());

        var resultat = await expediteur.EnvoyerAsync(Piece, confirme: true);

        Assert.False(resultat.Reussi);
        Assert.False(client.Appele);
        Assert.Contains("ne s'est pas constaté seul", resultat.Message);
    }

    [Fact]
    public async Task Une_base_muette_arrete_un_agent_qui_se_sait_accompagne()
    {
        var accompagne = new SuiviAgents();
        accompagne.Noter(autres: 1);

        var (expediteur, client, _) = Monter(
            new ReservationFeinte(SortReservation.Indisponible), agents: accompagne);

        Assert.False((await expediteur.EnvoyerAsync(Piece, confirme: true)).Reussi);
        Assert.False(client.Appele);
    }

    [Fact]
    public async Task Une_base_muette_n_arrete_pas_un_agent_constate_seul()
    {
        // Le point qui rend la conception défendable : un poste isolé — le cas
        // de l'immense majorité des installations — ne cesse pas de certifier
        // parce qu'un service distant est en panne. Son registre fichier est la
        // mémoire complète de tout ce qu'il a envoyé.
        var seul = new SuiviAgents();
        seul.Noter(autres: 0);

        var (expediteur, client, _) = Monter(
            new ReservationFeinte(SortReservation.Indisponible), agents: seul);

        Assert.True((await expediteur.EnvoyerAsync(Piece, confirme: true)).Reussi);
        Assert.True(client.Appele);
    }

    [Fact]
    public async Task Une_piece_refusee_reste_refusee_meme_pour_un_agent_seul()
    {
        // « Seul » n'autorise que le silence de la base. Un refus explicite —
        // la pièce est déjà partie — reste un refus, quoi qu'il arrive.
        var seul = new SuiviAgents();
        seul.Noter(autres: 0);

        var (expediteur, client, _) = Monter(
            new ReservationFeinte(SortReservation.Refusee), agents: seul);

        Assert.False((await expediteur.EnvoyerAsync(Piece, confirme: true)).Reussi);
        Assert.False(client.Appele);
    }

    // --- Ce qu'elle n'empêche pas -------------------------------------------

    [Fact]
    public async Task Une_piece_reservee_part_normalement()
    {
        var reservation = new ReservationFeinte(SortReservation.Obtenue);
        var (expediteur, client, _) = Monter(reservation);

        var resultat = await expediteur.EnvoyerAsync(Piece, confirme: true);

        Assert.True(resultat.Reussi);
        Assert.True(client.Appele);
        Assert.Single(reservation.Reservees);
    }

    [Fact]
    public async Task Sans_SaaS_le_registre_local_fait_seul_autorite()
    {
        // Un poste isolé — l'immense majorité des installations — continue
        // exactement comme avant. La réservation n'est pas un prérequis.
        var (expediteur, client, _) = Monter(new ReservationFeinte(SortReservation.SansObjet)
        {
            Actif = false,
        });

        Assert.True((await expediteur.EnvoyerAsync(Piece, confirme: true)).Reussi);
        Assert.True(client.Appele);
    }

    [Fact]
    public async Task Un_expediteur_sans_reservation_du_tout_fonctionne()
    {
        // Le paramètre est facultatif : les vingt sites d'appel qui existaient
        // avant continuent de compiler et de se comporter à l'identique.
        var (expediteur, client, _) = Monter(reservation: null);

        Assert.True((await expediteur.EnvoyerAsync(Piece, confirme: true)).Reussi);
        Assert.True(client.Appele);
    }

    // --- Ce qui se rend, et ce qui reste tenu -------------------------------

    [Fact]
    public async Task Un_refus_net_rend_la_piece_a_tout_le_monde()
    {
        // 4xx : la requête a été rejetée, rien n'a été créé chez la DGI. La
        // pièce peut repartir — d'ici ou d'un autre poste.
        var reservation = new ReservationFeinte(SortReservation.Obtenue);
        var (expediteur, _, _) = Monter(reservation,
            new FneSignResult(false, 400, Erreur: "la plateforme a répondu 400 Bad Request."));

        await expediteur.EnvoyerAsync(Piece, confirme: true);

        Assert.Single(reservation.Liberees);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(null)]
    public async Task Une_issue_inconnue_laisse_la_piece_reservee(int? code)
    {
        // La DGI a pu enregistrer la facture. La libérer autoriserait un second
        // envoi depuis un autre poste, et ce serait exactement le doublon que
        // tout ceci existe pour empêcher.
        var reservation = new ReservationFeinte(SortReservation.Obtenue);
        var (expediteur, _, _) = Monter(reservation,
            new FneSignResult(false, code, Erreur: "issue incertaine."));

        await expediteur.EnvoyerAsync(Piece, confirme: true);

        Assert.Empty(reservation.Liberees);
    }

    // --- Le point qui distingue cette conception d'un verrou par dossier ----

    [Fact]
    public async Task Deux_agents_peuvent_envoyer_deux_pieces_differentes()
    {
        // Rien n'oblige les agents à se mettre en file : seule la PIÈCE est
        // exclusive, jamais le dossier. Un verrou par dossier aurait interdit
        // ce qui suit, et c'eût été trop grossier.
        var reservation = new ReservationFeinte(SortReservation.Obtenue);
        var (premier, clientA, _) = Monter(reservation);
        var (second, clientB, _) = Monter(reservation);

        var a = premier.EnvoyerAsync("1221", confirme: true);
        var b = second.EnvoyerAsync("1220", confirme: true);
        await Task.WhenAll(a, b);

        Assert.True(clientA.Appele);
        Assert.True(clientB.Appele);
        Assert.Equal(2, reservation.Reservees.Distinct().Count());
    }

    // --- Le constat, et la panne pendant laquelle un poste s'ajoute ---------

    [Fact]
    public void Un_agent_qui_n_a_jamais_joint_la_base_ne_suppose_rien()
    {
        var suivi = new SuiviAgents();

        Assert.Equal(ConstatAgents.Inconnu, suivi.Dernier);
        Assert.False(suivi.PeutSePasserDeLaBase);
        Assert.Null(suivi.VuLe);
    }

    [Fact]
    public void Le_constat_suit_ce_que_la_base_a_dit()
    {
        var suivi = new SuiviAgents();

        suivi.Noter(autres: 0);
        Assert.Equal(ConstatAgents.Seul, suivi.Dernier);
        Assert.True(suivi.PeutSePasserDeLaBase);

        suivi.Noter(autres: 2);
        Assert.Equal(ConstatAgents.Accompagne, suivi.Dernier);
        Assert.False(suivi.PeutSePasserDeLaBase);
    }

    [Fact]
    public async Task Un_second_poste_installe_pendant_une_panne_n_envoie_rien()
    {
        // Le scénario qui décide de tout. L'ancien s'est constaté seul avant la
        // panne et continue ; le nouveau n'a jamais joint la base et s'arrête.
        // L'ordre d'apparition rend la chose sûre : un agent qui démarre voit
        // toujours celui qui était là avant lui, alors que l'inverse prend un
        // tour. Les deux ne peuvent donc pas envoyer la même pièce.
        var ancien = new SuiviAgents();
        ancien.Noter(autres: 0);

        var nouveau = new SuiviAgents();

        var muette = new ReservationFeinte(SortReservation.Indisponible);
        var (premier, clientAncien, _) = Monter(muette, agents: ancien);
        var (second, clientNouveau, _) = Monter(muette, agents: nouveau);

        var a = await premier.EnvoyerAsync(Piece, confirme: true);
        var b = await second.EnvoyerAsync(Piece, confirme: true);

        Assert.True(a.Reussi);
        Assert.True(clientAncien.Appele);

        Assert.False(b.Reussi);
        Assert.False(clientNouveau.Appele);
    }

    [Fact]
    public void Le_constat_se_perd_au_redemarrage_et_c_est_voulu()
    {
        // Un agent qui redémarre repart d'« inconnu », c'est-à-dire du
        // comportement prudent, jusqu'à ce que la base lui réponde. Persister
        // ce constat le ferait survivre à une situation qui a changé.
        var suivi = new SuiviAgents();
        suivi.Noter(autres: 0);

        Assert.Equal(ConstatAgents.Inconnu, new SuiviAgents().Dernier);
    }
}
