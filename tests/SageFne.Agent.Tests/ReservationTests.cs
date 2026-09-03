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
        IReservationClient? reservation, FneSignResult? reponse = null)
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
            lecteur, registre, client, NullLogger<InvoiceSender>.Instance, reglages, reservation),
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
    public async Task Une_base_muette_arrete_l_envoi_plutot_que_de_supposer()
    {
        // Ne pas pouvoir prouver qu'une pièce est libre n'autorise pas à la
        // croire libre. Un retard se rattrape au tour suivant ; un doublon
        // certifié ne se reprend que par un avoir.
        var (expediteur, client, _) = Monter(new ReservationFeinte(SortReservation.Indisponible));

        var resultat = await expediteur.EnvoyerAsync(Piece, confirme: true);

        Assert.False(resultat.Reussi);
        Assert.False(client.Appele);
        Assert.Contains("repassera au tour suivant", resultat.Message);
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
}
