using Microsoft.Extensions.Options;
using SageFne.Reader.Batch;
using SageFne.Reader.Certification;
using SageFne.Reader.Configuration;
using SageFne.Reader.Data;
using SageFne.Reader.Mapping;
using SageFne.Reader.Models.Sage;

namespace SageFne.Reader.Tests;

public class InvoiceBatchReaderTests
{
    /// <summary>
    /// Dépôt d'essai qui compte ses appels : c'est la seule façon de vérifier,
    /// sans base, que le lot ne fait pas un aller-retour par facture.
    /// </summary>
    private sealed class DepotCompteur : ISageInvoiceRepository
    {
        public int AppelsEntetes { get; private set; }
        public int AppelsLignes { get; private set; }
        public int AppelsClients { get; private set; }
        public int AppelsUnitaires { get; private set; }

        public List<SageDocumentHeader> Entetes { get; init; } = [];
        public List<SageDocumentLine> Lignes { get; init; } = [];
        public List<SageCustomer> Clients { get; init; } = [];

        public Task<List<SageDocumentHeader>> GetInvoicesAsync(InvoiceQuery query, CancellationToken ct = default)
        {
            AppelsEntetes++;
            return Task.FromResult(Entetes
                .Where(entete => query.Pieces.Count == 0 || query.Pieces.Contains(entete.Piece))
                .Take(query.Limite)
                .ToList());
        }

        public Task<List<SageDocumentLine>> GetLinesAsync(InvoiceQuery query, CancellationToken ct = default)
        {
            AppelsLignes++;
            return Task.FromResult(Lignes
                .Where(ligne => query.Pieces.Count == 0 || query.Pieces.Contains(ligne.Piece))
                .ToList());
        }

        public Task<List<SageCustomer>> GetCustomersAsync(IReadOnlyCollection<string> ctNums, CancellationToken ct = default)
        {
            AppelsClients++;
            return Task.FromResult(Clients.Where(client => ctNums.Contains(client.CtNum)).ToList());
        }

        public Task<SageDocumentHeader?> GetInvoiceAsync(string piece, CancellationToken ct = default)
        {
            AppelsUnitaires++;
            return Task.FromResult(Entetes.FirstOrDefault(entete => entete.Piece == piece));
        }

        public Task<List<SageDocumentLine>> GetInvoiceLinesAsync(string piece, CancellationToken ct = default)
        {
            AppelsUnitaires++;
            return Task.FromResult(Lignes.Where(ligne => ligne.Piece == piece).ToList());
        }

        public Task<SageCustomer?> GetCustomerAsync(string ctNum, CancellationToken ct = default)
        {
            AppelsUnitaires++;
            return Task.FromResult(Clients.FirstOrDefault(client => client.CtNum == ctNum));
        }

        public Task<List<SageTaxDefinition>> GetTaxesAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<SageTaxDefinition>());
    }

    private static SageDocumentHeader Entete(string piece, string tiers, int jour) => new()
    {
        Domaine = 0,
        Type = 6,
        Piece = piece,
        Date = new DateTime(2025, 12, jour),
        Tiers = tiers,
        TotalHT = 0m,
    };

    private static SageDocumentLine Ligne(string piece, int rang, decimal ht) => new()
    {
        Domaine = 0,
        Type = 6,
        Piece = piece,
        Ligne = rang,
        ArticleReference = $"ART-{rang}",
        Designation = $"Article {rang}",
        Quantite = 1m,
        PrixUnitaire = ht,
        MontantHT = ht,
        MontantTTC = ht,
        Unite = "KG",
    };

    private static SageCustomer Client(string ctNum, string ncc = "14322625") => new()
    {
        CtNum = ctNum,
        Intitule = $"Client {ctNum}",
        Identifiant = ncc,
    };

    /// <summary>Registre d'essai qui compte ses lectures.</summary>
    internal sealed class RegistreCompteur : ICertificationLedger
    {
        public int Lectures { get; private set; }
        public Dictionary<string, CertifiedInvoice> Entrees { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyDictionary<string, CertifiedInvoice>> LookupAsync(
            IReadOnlyCollection<string> pieces,
            CancellationToken ct = default)
        {
            Lectures++;
            return Task.FromResult<IReadOnlyDictionary<string, CertifiedInvoice>>(
                pieces.Where(Entrees.ContainsKey).ToDictionary(piece => piece, piece => Entrees[piece]));
        }

        public Task RecordAsync(CertifiedInvoice certification, CancellationToken ct = default)
        {
            Entrees[certification.Piece] = certification;
            return Task.CompletedTask;
        }
    }

    private static InvoiceBatchReader Lecteur(
        ISageInvoiceRepository depot,
        ICertificationLedger? registre = null)
    {
        var options = Options.Create(new FneOptions { Template = "B2B", PaymentMethod = "deferred" });
        return new InvoiceBatchReader(
            depot,
            new FneInvoiceMapper(options),
            registre ?? new RegistreCompteur(),
            options);
    }

    private static DepotCompteur DepotDeTrois() => new()
    {
        Entetes = [Entete("1", "C1", 3), Entete("2", "C2", 4), Entete("3", "C1", 5)],
        Lignes =
        [
            Ligne("1", 1, 1000m),
            Ligne("2", 1, 2000m), Ligne("2", 2, 3000m),
            Ligne("3", 1, 4000m),
        ],
        Clients = [Client("C1"), Client("C2")],
    };

    [Fact]
    public async Task Le_lot_tient_en_trois_lectures_quel_que_soit_le_nombre_de_factures()
    {
        var depot = DepotDeTrois();

        await Lecteur(depot).ReadAsync(new InvoiceQuery());

        // Trois factures, mais une lecture d'entêtes, une de lignes, une de
        // clients — et aucune lecture pièce par pièce.
        Assert.Equal(1, depot.AppelsEntetes);
        Assert.Equal(1, depot.AppelsLignes);
        Assert.Equal(1, depot.AppelsClients);
        Assert.Equal(0, depot.AppelsUnitaires);
    }

    [Fact]
    public async Task Chaque_facture_recoit_ses_propres_lignes()
    {
        var lot = await Lecteur(DepotDeTrois()).ReadAsync(new InvoiceQuery());

        Assert.Equal(3, lot.Total);
        Assert.Equal([1, 2, 1], lot.Conversions.Select(conversion => conversion.Lines.Count));
        Assert.Equal(4, lot.Lignes);
        Assert.All(lot.Conversions, conversion =>
            Assert.All(conversion.Lines, ligne => Assert.Equal(conversion.Header.Piece, ligne.Piece)));
    }

    [Fact]
    public async Task Les_factures_portent_le_bon_client()
    {
        var lot = await Lecteur(DepotDeTrois()).ReadAsync(new InvoiceQuery());

        Assert.Equal(["C1", "C2", "C1"], lot.Conversions.Select(conversion => conversion.Customer!.CtNum));
    }

    [Fact]
    public async Task Une_piece_en_defaut_n_arrete_pas_le_lot()
    {
        var depot = DepotDeTrois();
        // Le client de la deuxième pièce n'a pas de NCC.
        depot.Clients[1] = Client("C2", ncc: "");

        var lot = await Lecteur(depot).ReadAsync(new InvoiceQuery());

        Assert.Equal(3, lot.Total);
        Assert.Equal(2, lot.ACertifier);
        Assert.Equal(1, lot.Bloquees);
        Assert.Contains(lot.Conversions[1].Report.Constats, constat => constat.Code == "NCC_MANQUANT");
        // Les deux autres sont bien traduites.
        Assert.NotNull(lot.Conversions[0].Invoice);
        Assert.NotNull(lot.Conversions[2].Invoice);
    }

    [Fact]
    public async Task Un_client_introuvable_bloque_sa_piece_sans_planter()
    {
        var depot = DepotDeTrois();
        depot.Clients.RemoveAll(client => client.CtNum == "C2");

        var lot = await Lecteur(depot).ReadAsync(new InvoiceQuery());

        Assert.Null(lot.Conversions[1].Invoice);
        Assert.Contains(lot.Conversions[1].Report.Constats, constat => constat.Code == "CLIENT_INTROUVABLE");
        Assert.Equal(2, lot.ACertifier);
    }

    [Fact]
    public async Task Une_piece_sans_ligne_est_signalee()
    {
        var depot = DepotDeTrois();
        depot.Lignes.RemoveAll(ligne => ligne.Piece == "3");

        var lot = await Lecteur(depot).ReadAsync(new InvoiceQuery());

        Assert.Contains(lot.Conversions[2].Report.Constats, constat => constat.Code == "SANS_LIGNE");
        Assert.Null(lot.Conversions[2].Invoice);
    }

    [Fact]
    public async Task Un_lot_vide_le_dit_sans_echouer()
    {
        var lot = await Lecteur(new DepotCompteur()).ReadAsync(new InvoiceQuery { Pieces = ["9999"] });

        Assert.Equal(0, lot.Total);
        Assert.Contains(lot.Constats, constat => constat.Code == "LOT_VIDE");
    }

    [Fact]
    public async Task Atteindre_la_limite_est_signale()
    {
        var lot = await Lecteur(DepotDeTrois()).ReadAsync(new InvoiceQuery { Limite = 3 });

        Assert.Equal(3, lot.Total);
        Assert.Contains(lot.Constats, constat => constat.Code == "LIMITE_ATTEINTE");
    }

    [Fact]
    public async Task Les_totaux_du_lot_sont_ceux_des_lignes()
    {
        var lot = await Lecteur(DepotDeTrois()).ReadAsync(new InvoiceQuery());

        Assert.Equal(10000m, lot.TotalHT);
    }
}
