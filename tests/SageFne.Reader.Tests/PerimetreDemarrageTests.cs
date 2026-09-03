using Microsoft.Extensions.Options;
using SageFne.Core.Batch;
using SageFne.Core.Certification;
using SageFne.Core.Configuration;
using SageFne.Core.Data;
using SageFne.Core.Mapping;
using SageFne.Core.Models.Sage;
using SageFne.Core.Validation;

namespace SageFne.Core.Tests;

/// <summary>
/// Le périmètre : à partir de quelle date le middleware se sent concerné.
/// </summary>
/// <remarks>
/// Le dossier porte mille quatre pièces facturées avant que la FNE n'existe
/// pour lui. Elles ne sont pas à certifier rétroactivement, et beaucoup ne le
/// pourraient pas — NCC absent, téléphone absent, quantité nulle.
///
/// Une fenêtre glissante les écarte, mais par accident : élargir
/// <c>Agent:FenetreJours</c> les ramènerait toutes, et l'agent annoncerait
/// « mille pièces bloquées ». Une date de démarrage les écarte par décision, et
/// l'écrit.
/// </remarks>
public class PerimetreDemarrageTests
{
    private static readonly DateTime Demarrage = new(2026, 9, 1);

    private sealed class Depot : ISageInvoiceRepository
    {
        /// <summary>La doublure tient lieu de vrai dossier : elle peut envoyer.</summary>
        public bool EstReel => true;

        public Task<List<SageDomaineSummary>> GetDomainesAsync(
            CancellationToken cancellation = default) =>
            Task.FromResult(new List<SageDomaineSummary>());

        public List<SageDocumentHeader> Entetes { get; init; } = [];
        public List<SageDocumentLine> Lignes { get; init; } = [];
        public List<SageCustomer> Clients { get; init; } = [];

        public Task<List<SageDocumentHeader>> GetInvoicesAsync(InvoiceQuery q, CancellationToken ct = default) =>
            Task.FromResult(Entetes.ToList());

        public Task<List<SageDocumentLine>> GetLinesAsync(InvoiceQuery q, CancellationToken ct = default) =>
            Task.FromResult(Lignes.ToList());

        public Task<List<SageCustomer>> GetCustomersAsync(IReadOnlyCollection<string> n, CancellationToken ct = default) =>
            Task.FromResult(Clients.Where(client => n.Contains(client.CtNum)).ToList());

        public Task<SageDocumentHeader?> GetInvoiceAsync(string p, CancellationToken ct = default) =>
            Task.FromResult(Entetes.FirstOrDefault(entete => entete.Piece == p));

        public Task<List<SageDocumentLine>> GetInvoiceLinesAsync(string p, CancellationToken ct = default) =>
            Task.FromResult(Lignes.Where(ligne => ligne.Piece == p).ToList());

        public Task<SageCustomer?> GetCustomerAsync(string c, CancellationToken ct = default) =>
            Task.FromResult(Clients.FirstOrDefault(client => client.CtNum == c));

        public Task<List<SageTaxDefinition>> GetTaxesAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<SageTaxDefinition>());

        public Task<List<SageDocumentTypeSummary>> GetDocumentTypesAsync(int e = 5, CancellationToken ct = default) =>
            Task.FromResult(new List<SageDocumentTypeSummary>());

        public Task<List<SageDocumentHeader>> GetDocumentsByPieceAsync(string p, CancellationToken ct = default) =>
            Task.FromResult(Entetes.Where(entete => entete.Piece == p).ToList());

        public Task<List<SageDocumentDuplicate>> GetPiecesMultiTypesAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<SageDocumentDuplicate>());

        public Task<List<SageColonnesManquantes>> GetColonnesManquantesAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<SageColonnesManquantes>());

        public Task<Dictionary<string, string>> GetArticleFamiliesAsync(
            IReadOnlyCollection<string> r, CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Une pièce ordinaire, conforme, à la date qu'on lui donne.</summary>
    private static Depot DepotDUnePiece(DateTime date) => new()
    {
        Entetes =
        [
            new() { Domaine = 0, Type = 6, Piece = "1221", Date = date, Tiers = "4111GEMS" },
        ],
        Lignes =
        [
            new()
            {
                Domaine = 0, Type = 6, Piece = "1221", Ligne = 1,
                ArticleReference = "ART1", Designation = "Prestation",
                Quantite = 1m, PrixUnitaire = 1000m, Unite = "U",
                MontantHT = 1000m, MontantTTC = 1180m, Taxe1 = 18m,
            },
        ],
        Clients =
        [
            new()
            {
                CtNum = "4111GEMS", Intitule = "GEMS-CI",
                Identifiant = "1432262S", Telephone = "0700000000",
            },
        ],
    };

    private static async Task<InvoiceBatch> Lire(DateTime dateDeLaPiece, DateTime? demarrage)
    {
        var reglages = Options.Create(new FneOptions
        {
            Template = "B2B",
            PaymentMethod = "deferred",
            DemarrageLe = demarrage,
        });

        var lecteur = new InvoiceBatchReader(
            DepotDUnePiece(dateDeLaPiece),
            new FneInvoiceMapper(reglages),
            new DemoCertificationLedger(),
            reglages);

        return await lecteur.ReadAsync(new InvoiceQuery());
    }

    [Fact]
    public async Task Une_piece_anterieure_au_demarrage_est_hors_perimetre_et_non_bloquee()
    {
        var lot = await Lire(new DateTime(2024, 5, 12), Demarrage);
        var piece = Assert.Single(lot.Conversions);

        Assert.Equal(EtatPiece.HorsPerimetre, piece.Etat);

        // Le point qui compte : ce n'est pas un blocage. Les confondre ferait
        // passer un historique écarté volontairement pour un lot de factures à
        // réparer, et l'on chercherait indéfiniment quoi corriger dessus.
        Assert.Equal(0, lot.Bloquees);
        Assert.Equal(1, lot.HorsPerimetre);
        Assert.Contains(piece.Report.Constats, c => c.Code == "ANTERIEURE_AU_DEMARRAGE");
        Assert.DoesNotContain(piece.Report.Constats, c => c.Severite == Severite.Erreur);
    }

    [Fact]
    public async Task Le_jour_du_demarrage_est_dans_le_perimetre()
    {
        // La borne est inclusive : « à partir du 1er septembre » comprend le
        // 1er septembre. Exclusive, elle perdrait la première journée de
        // production sans que personne ne s'en aperçoive.
        var lot = await Lire(Demarrage, Demarrage);

        Assert.Equal(0, lot.HorsPerimetre);
        Assert.Equal(1, lot.ACertifier);
    }

    [Fact]
    public async Task L_heure_de_saisie_ne_fait_pas_sortir_du_perimetre()
    {
        // DO_Date porte parfois une heure. Comparer des instants écarterait une
        // facture saisie le matin du jour de démarrage.
        var lot = await Lire(Demarrage.AddHours(3), Demarrage);

        Assert.Equal(0, lot.HorsPerimetre);
    }

    [Fact]
    public async Task La_veille_du_demarrage_reste_dehors()
    {
        var lot = await Lire(Demarrage.AddDays(-1), Demarrage);

        Assert.Equal(1, lot.HorsPerimetre);
    }

    [Fact]
    public async Task Sans_date_de_demarrage_tout_le_dossier_reste_dans_le_perimetre()
    {
        // Le défaut ne change pas le comportement d'origine : poser un plancher
        // est un choix, il ne s'impose pas à qui n'en veut pas.
        var lot = await Lire(new DateTime(2019, 1, 1), null);

        Assert.Equal(0, lot.HorsPerimetre);
        Assert.Equal(1, lot.ACertifier);
    }
}
