using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SageFne.Reader.Batch;
using SageFne.Reader.Certification;
using SageFne.Reader.Configuration;
using SageFne.Reader.Data;
using SageFne.Reader.Mapping;
using SageFne.Reader.Models.Sage;
using SageFne.Reader.Validation;

namespace SageFne.Reader.Tests;

/// <summary>
/// Renvoyer à la DGI une facture déjà certifiée créerait un doublon
/// irrattrapable. Ces tests tiennent la règle qui l'empêche.
/// </summary>
public class CertificationTests
{
    private static readonly FneOptions Reglages = new() { Template = "B2B", PaymentMethod = "deferred" };

    private sealed class Depot : ISageInvoiceRepository
    {
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
    }

    private static Depot DepotDUnePiece(decimal prixUnitaire = 2500m) => new()
    {
        Entetes =
        [
            new()
            {
                Domaine = 0, Type = 6, Piece = "1219",
                Date = new DateTime(2025, 12, 3), Tiers = "4111SITASARL",
            },
        ],
        Lignes =
        [
            new()
            {
                Domaine = 0, Type = 6, Piece = "1219", Ligne = 1,
                ArticleReference = "13415001", Designation = "Queue De Boeuf PV - Friboi",
                Quantite = 196.39m, PrixUnitaire = prixUnitaire, Unite = "KG",
                MontantHT = 196.39m * prixUnitaire, MontantTTC = 196.39m * prixUnitaire,
                Taxe2 = 1.5m, CodeTaxe2 = "AIRSI",
            },
        ],
        Clients = [new() { CtNum = "4111SITASARL", Intitule = "SITA SARL", Identifiant = "14322625" }],
    };

    private static InvoiceBatchReader Lecteur(ISageInvoiceRepository depot, ICertificationLedger registre)
    {
        var options = Options.Create(Reglages);
        return new InvoiceBatchReader(depot, new FneInvoiceMapper(options), registre, options);
    }

    private static async Task<InvoiceConversion> Convertir(Depot depot, ICertificationLedger registre)
    {
        var lot = await Lecteur(depot, registre).ReadAsync(new InvoiceQuery());
        return lot.Conversions.Single();
    }

    [Fact]
    public async Task Une_piece_inconnue_du_registre_est_a_certifier()
    {
        var conversion = await Convertir(DepotDUnePiece(), new InvoiceBatchReaderTests.RegistreCompteur());

        Assert.Equal(EtatPiece.ACertifier, conversion.Etat);
        Assert.Null(conversion.Certification);
        Assert.NotEqual("", conversion.Empreinte);
    }

    [Fact]
    public async Task Une_piece_deja_certifiee_et_inchangee_ne_repart_pas()
    {
        var depot = DepotDUnePiece();
        var registre = new InvoiceBatchReaderTests.RegistreCompteur();

        // Première passe : on relève l'empreinte, comme le ferait l'envoi.
        var premiere = await Convertir(depot, registre);
        await registre.RecordAsync(new CertifiedInvoice
        {
            Identite = "0/6/1219",
            Piece = "1219",
            ReferenceFne = "2304903U26000000930",
            CertifieeLe = DateTimeOffset.Now.AddDays(-1),
            Empreinte = premiere.Empreinte,
        });

        var seconde = await Convertir(depot, registre);

        Assert.Equal(EtatPiece.DejaCertifiee, seconde.Etat);
        Assert.False(seconde.EstPrete);
        Assert.Equal("2304903U26000000930", seconde.Certification!.ReferenceFne);
        var constat = Assert.Single(seconde.Report.Constats, c => c.Code == "DEJA_CERTIFIEE");
        Assert.Equal(Severite.Avertissement, constat.Severite);
    }

    [Fact]
    public async Task Une_piece_modifiee_apres_certification_est_une_erreur()
    {
        var registre = new InvoiceBatchReaderTests.RegistreCompteur();
        var avant = await Convertir(DepotDUnePiece(prixUnitaire: 2500m), registre);
        await registre.RecordAsync(new CertifiedInvoice
        {
            Identite = "0/6/1219",
            Piece = "1219",
            CertifieeLe = DateTimeOffset.Now.AddDays(-1),
            Empreinte = avant.Empreinte,
        });

        // Le prix a changé dans Sage depuis la certification.
        var apres = await Convertir(DepotDUnePiece(prixUnitaire: 2600m), registre);

        Assert.Equal(EtatPiece.ModifieeDepuis, apres.Etat);
        Assert.True(apres.Report.ContientDesErreurs);
        Assert.Contains(apres.Report.Constats, c => c.Code == "MODIFIEE_APRES_CERTIFICATION");
    }

    [Fact]
    public async Task Le_registre_est_lu_une_seule_fois_par_lot()
    {
        var registre = new InvoiceBatchReaderTests.RegistreCompteur();

        await Lecteur(DepotDUnePiece(), registre).ReadAsync(new InvoiceQuery());

        Assert.Equal(1, registre.Lectures);
    }

    [Fact]
    public void Deux_traductions_identiques_donnent_la_meme_empreinte()
    {
        var options = Options.Create(Reglages);
        var mappeur = new FneInvoiceMapper(options);
        var depot = DepotDUnePiece();
        var entete = depot.Entetes[0];
        var client = depot.Clients[0];

        var premiere = InvoiceFingerprint.Compute(mappeur.Map(entete, depot.Lignes, client));
        var seconde = InvoiceFingerprint.Compute(mappeur.Map(entete, depot.Lignes, client));
        var autre = InvoiceFingerprint.Compute(
            mappeur.Map(entete, DepotDUnePiece(prixUnitaire: 2600m).Lignes, client));

        Assert.Equal(premiere, seconde);
        Assert.NotEqual(premiere, autre);
        Assert.Equal(64, premiere.Length); // SHA-256 en hexadécimal
    }
}

public class JsonCertificationLedgerTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), $"registre-{Guid.NewGuid():N}");

    private JsonCertificationLedger Registre(string nom = "certifications.json") =>
        new(Path.Combine(_dossier, nom), NullLogger<JsonCertificationLedger>.Instance);

    private static CertifiedInvoice Entree(string piece, string empreinte = "abc") => new()
    {
        Identite = $"0/6/{piece}",
        Piece = piece,
        ReferenceFne = $"REF{piece}",
        CertifieeLe = new DateTimeOffset(2025, 12, 3, 10, 0, 0, TimeSpan.Zero),
        Empreinte = empreinte,
    };

    [Fact]
    public async Task Un_registre_absent_ne_connait_rien_et_ne_leve_pas()
    {
        var connues = await Registre().LookupAsync(["1219"]);

        Assert.Empty(connues);
    }

    [Fact]
    public async Task Ce_qui_est_inscrit_se_relit()
    {
        var registre = Registre();
        await registre.RecordAsync(Entree("1219"));
        await registre.RecordAsync(Entree("1220"));

        var connues = await Registre().LookupAsync(["0/6/1219", "0/6/1221"]);

        Assert.Single(connues);
        Assert.Equal("REF1219", connues["0/6/1219"].ReferenceFne);
    }

    [Fact]
    public async Task Reinscrire_une_piece_remplace_sa_trace()
    {
        var registre = Registre();
        await registre.RecordAsync(Entree("1219", empreinte: "avant"));
        await registre.RecordAsync(Entree("1219", empreinte: "apres"));

        var connues = await registre.LookupAsync(["0/6/1219"]);

        Assert.Equal("apres", Assert.Single(connues.Values).Empreinte);
    }

    [Fact]
    public async Task Un_registre_illisible_est_signale_sans_arreter_le_lot()
    {
        Directory.CreateDirectory(_dossier);
        var chemin = Path.Combine(_dossier, "casse.json");
        await File.WriteAllTextAsync(chemin, "{ ceci n'est pas du JSON");

        var registre = new JsonCertificationLedger(chemin, NullLogger<JsonCertificationLedger>.Instance);
        var connues = await registre.LookupAsync(["1219"]);

        // Traité comme vide : mieux vaut proposer de recertifier — l'exploitant
        // le verra — que d'interrompre le traitement.
        Assert.Empty(connues);
        Assert.True(registre.EstIllisible);
    }

    [Fact]
    public async Task L_ecriture_ne_laisse_pas_de_fichier_provisoire()
    {
        var registre = Registre();
        await registre.RecordAsync(Entree("1219"));

        Assert.True(File.Exists(registre.Chemin));
        Assert.Empty(Directory.GetFiles(_dossier, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dossier)) Directory.Delete(_dossier, recursive: true);
        GC.SuppressFinalize(this);
    }
}
