using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SageFne.Reader.Batch;
using SageFne.Reader.Certification;
using SageFne.Reader.Configuration;
using SageFne.Reader.Data;
using SageFne.Reader.Fne;
using SageFne.Reader.Mapping;
using SageFne.Reader.Models.Fne;

namespace SageFne.Reader.Tests;

/// <summary>
/// Ce que le registre dit d'une pièce décide si elle peut repartir.
/// </summary>
/// <remarks>
/// La première version ne comparait que les empreintes : une tentative
/// <c>Error</c> portant la même empreinte que la pièce ressortait « déjà
/// certifiée », et bloquait tout renvoi alors que rien n'avait été certifié.
/// Un refus de la plateforme condamnait la facture.
/// </remarks>
public class RegistreEtatTests
{
    /// <summary>Registre pré-rempli, qui note aussi ce qu'on lui fait écrire.</summary>
    private sealed class RegistreSeme(params CertifiedInvoice[] entrees) : ICertificationLedger
    {
        private readonly Dictionary<string, CertifiedInvoice> _entrees =
            entrees.ToDictionary(entree => entree.Identite, StringComparer.OrdinalIgnoreCase);

        public List<CertifiedInvoice> Ecritures { get; } = [];

        public Task<IReadOnlyDictionary<string, CertifiedInvoice>> LookupAsync(
            IReadOnlyCollection<string> identites, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, CertifiedInvoice>>(
                identites.Where(_entrees.ContainsKey)
                    .ToDictionary(identite => identite, identite => _entrees[identite]));

        public Task RecordAsync(CertifiedInvoice certification, CancellationToken ct = default)
        {
            Ecritures.Add(certification);
            _entrees[certification.Identite] = certification;
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

    /// <summary>1221 : TVA 9 %, NCC présent — rien ne la bloque par elle-même.</summary>
    private const string Piece = "1221";
    private const string Identite = $"0/6/{Piece}";

    private static IOptions<FneOptions> Reglages() => Options.Create(new FneOptions
    {
        PointOfSale = "FISH-AFRIC",
        Establishment = "FISH-AFRIC",
        Template = "B2B",
        PaymentMethod = "deferred",
    });

    private static InvoiceBatchReader Lecteur(ICertificationLedger registre)
    {
        var reglages = Reglages();
        return new InvoiceBatchReader(
            new DemoSageInvoiceRepository(), new FneInvoiceMapper(reglages), registre, reglages);
    }

    /// <summary>
    /// L'empreinte réelle de la pièce, telle que l'envoi l'aurait inscrite.
    /// Une empreinte inventée ne prouverait rien : c'est justement l'égalité
    /// des empreintes qui rendait le défaut invisible.
    /// </summary>
    private static async Task<string> EmpreinteReelle()
    {
        var lot = await Lecteur(new RegistreSeme()).ReadAsync(InvoiceQuery.Piece(Piece));
        return lot.Conversions[0].Empreinte;
    }

    private static CertifiedInvoice Trace(EtatFne etat, string empreinte, string reference = "") => new()
    {
        Identite = Identite,
        Piece = Piece,
        Empreinte = empreinte,
        Etat = etat,
        ReferenceFne = reference,
        CertifieeLe = DateTimeOffset.Now.AddMinutes(-10),
    };

    private static async Task<InvoiceConversion> Lire(params CertifiedInvoice[] entrees)
    {
        var lot = await Lecteur(new RegistreSeme(entrees)).ReadAsync(InvoiceQuery.Piece(Piece));
        return lot.Conversions[0];
    }

    [Fact]
    public async Task Une_tentative_en_erreur_laisse_la_piece_repartir()
    {
        // Le cas réel : la DGI a répondu 500, rien n'a été certifié, et la
        // facture doit pouvoir repartir une fois la cause corrigée.
        var conversion = await Lire(Trace(EtatFne.Error, await EmpreinteReelle()));

        Assert.Equal(EtatPiece.ACertifier, conversion.Etat);
        Assert.Contains(conversion.Report.Constats, constat => constat.Code == "TENTATIVE_PRECEDENTE");
        Assert.DoesNotContain(conversion.Report.Constats, constat => constat.Code == "DEJA_CERTIFIEE");
    }

    [Fact]
    public async Task Un_envoi_en_suspens_met_la_piece_en_suspens()
    {
        var conversion = await Lire(Trace(EtatFne.Sending, await EmpreinteReelle()));

        Assert.Equal(EtatPiece.EnSuspens, conversion.Etat);
        Assert.Contains(conversion.Report.Constats, constat => constat.Code == "ENVOI_EN_SUSPENS");
    }

    [Fact]
    public async Task Une_certification_inchangee_bloque_toujours()
    {
        // Le garde-fou d'origine ne doit pas disparaître avec la correction.
        var conversion = await Lire(Trace(EtatFne.Certified, await EmpreinteReelle(), "REF-1"));

        Assert.Equal(EtatPiece.DejaCertifiee, conversion.Etat);
    }

    [Fact]
    public async Task Une_certification_dont_l_empreinte_a_change_reste_signalee()
    {
        var conversion = await Lire(Trace(EtatFne.Certified, "empreinte-d-avant", "REF-1"));

        Assert.Equal(EtatPiece.ModifieeDepuis, conversion.Etat);
    }

    [Fact]
    public async Task Une_piece_en_suspens_ne_repart_pas()
    {
        var registre = new RegistreSeme(Trace(EtatFne.Sending, await EmpreinteReelle()));
        var client = new ClientTemoin(new FneSignResult(true, 200, "REF"));
        var expediteur = new InvoiceSender(
            Lecteur(registre), registre, client, NullLogger<InvoiceSender>.Instance);

        var resultat = await expediteur.EnvoyerAsync(Piece, confirme: true);

        Assert.False(client.Appele, "un envoi en suspens ne doit jamais repartir tout seul");
        Assert.Empty(registre.Ecritures);
        Assert.Equal(EtatFne.Sending, resultat.Etat);
        Assert.Contains("portail DGI", resultat.Message);
    }

    [Fact]
    public async Task Apres_un_echec_la_piece_corrigee_peut_repartir()
    {
        var registre = new RegistreSeme(Trace(EtatFne.Error, await EmpreinteReelle()));
        var client = new ClientTemoin(new FneSignResult(true, 200, "REF-2"));
        var expediteur = new InvoiceSender(
            Lecteur(registre), registre, client, NullLogger<InvoiceSender>.Instance);

        var resultat = await expediteur.EnvoyerAsync(Piece, confirme: true);

        Assert.True(client.Appele);
        Assert.Equal(EtatFne.Certified, resultat.Etat);
        Assert.Equal("REF-2", registre.Ecritures[^1].ReferenceFne);
    }
}

/// <summary>
/// Une réponse 5xx ne dit pas ce que la plateforme a enregistré : elle a pu
/// persister la facture avant d'échouer. Un refus 4xx, lui, est net.
/// </summary>
public class IssueDouteuseTests
{
    private sealed class RegistreEspion : ICertificationLedger
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

    private sealed class ClientFige(FneSignResult reponse) : IFneApiClient
    {
        public bool Reel => false;
        public string DecrireRequete(FneInvoice facture) => "";
        public Task<FneSignResult> SignAsync(FneInvoice facture, CancellationToken ct = default) =>
            Task.FromResult(reponse);
    }

    private static async Task<EtatFne> Envoyer(FneSignResult reponse)
    {
        var registre = new RegistreEspion();
        var reglages = Options.Create(new FneOptions
        {
            PointOfSale = "FISH-AFRIC",
            Establishment = "FISH-AFRIC",
            Template = "B2B",
            PaymentMethod = "deferred",
        });
        var lecteur = new InvoiceBatchReader(
            new DemoSageInvoiceRepository(), new FneInvoiceMapper(reglages), registre, reglages);
        var expediteur = new InvoiceSender(
            lecteur, registre, new ClientFige(reponse), NullLogger<InvoiceSender>.Instance);

        var resultat = await expediteur.EnvoyerAsync("1221", confirme: true);

        Assert.Equal(resultat.Etat, registre.Ecritures[^1].Etat);
        return resultat.Etat;
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task Une_erreur_serveur_laisse_la_piece_en_suspens(int code)
    {
        // La DGI a répondu « Error signing invoice » en 500 : nul ne sait si
        // elle avait déjà enregistré la facture avant d'échouer.
        var etat = await Envoyer(new FneSignResult(
            false, code, CorpsBrut: """{"error":"invoice_signing_error"}""", Erreur: "erreur serveur"));

        Assert.Equal(EtatFne.Sending, etat);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(422)]
    public async Task Un_refus_client_reste_une_erreur(int code)
    {
        // La requête a été rejetée : rien n'a été créé, la pièce peut repartir.
        var etat = await Envoyer(new FneSignResult(false, code, CorpsBrut: "{}", Erreur: "refus"));

        Assert.Equal(EtatFne.Error, etat);
    }
}

/// <summary>
/// Sortir une pièce du suspens : la seule source est le portail de la DGI, et
/// l'outil refuse de deviner à la place de l'exploitant.
/// </summary>
public class DeblocageTests
{
    private sealed class Registre(CertifiedInvoice? entree) : ICertificationLedger
    {
        private readonly Dictionary<string, CertifiedInvoice> _entrees =
            entree is null
                ? new Dictionary<string, CertifiedInvoice>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, CertifiedInvoice>(StringComparer.OrdinalIgnoreCase)
                    { [entree.Identite] = entree };

        public List<CertifiedInvoice> Ecritures { get; } = [];

        public Task<IReadOnlyDictionary<string, CertifiedInvoice>> LookupAsync(
            IReadOnlyCollection<string> identites, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, CertifiedInvoice>>(
                identites.Where(_entrees.ContainsKey)
                    .ToDictionary(identite => identite, identite => _entrees[identite]));

        public Task RecordAsync(CertifiedInvoice certification, CancellationToken ct = default)
        {
            Ecritures.Add(certification);
            _entrees[certification.Identite] = certification;
            return Task.CompletedTask;
        }
    }

    private sealed class ClientInterdit : IFneApiClient
    {
        public bool Reel => false;
        public string DecrireRequete(FneInvoice facture) => "";

        public Task<FneSignResult> SignAsync(FneInvoice facture, CancellationToken ct = default) =>
            throw new InvalidOperationException("debloquer ne doit appeler aucune API");
    }

    private const string Piece = "1221";

    private static CertifiedInvoice Trace(EtatFne etat, string reference = "") => new()
    {
        Identite = $"0/6/{Piece}",
        Piece = Piece,
        Empreinte = "peu importe",
        Etat = etat,
        ReferenceFne = reference,
        CertifieeLe = DateTimeOffset.Now.AddHours(-1),
    };

    private static (InvoiceSender Expediteur, Registre Registre) Monter(CertifiedInvoice? entree)
    {
        var registre = new Registre(entree);
        var reglages = Options.Create(new FneOptions
        {
            PointOfSale = "FISH-AFRIC",
            Establishment = "FISH-AFRIC",
            Template = "B2B",
            PaymentMethod = "deferred",
        });
        var lecteur = new InvoiceBatchReader(
            new DemoSageInvoiceRepository(), new FneInvoiceMapper(reglages), registre, reglages);

        return (new InvoiceSender(
            lecteur, registre, new ClientInterdit(), NullLogger<InvoiceSender>.Instance), registre);
    }

    [Fact]
    public async Task Sans_constat_du_portail_rien_ne_se_debloque()
    {
        var (expediteur, registre) = Monter(Trace(EtatFne.Sending));

        var resultat = await expediteur.DebloquerAsync(Piece, null, false, confirme: true);

        Assert.False(resultat.Applique);
        Assert.False(resultat.ConfirmationManque, "--confirmer ne débloquerait rien ici");
        Assert.Empty(registre.Ecritures);
        Assert.Contains("portail", resultat.Message);
    }

    [Fact]
    public async Task Les_deux_constats_a_la_fois_sont_refuses()
    {
        var (expediteur, registre) = Monter(Trace(EtatFne.Sending));

        var resultat = await expediteur.DebloquerAsync(Piece, "REF", true, confirme: true);

        Assert.False(resultat.Applique);
        Assert.Empty(registre.Ecritures);
    }

    [Fact]
    public async Task Sans_confirmation_rien_n_est_inscrit()
    {
        var (expediteur, registre) = Monter(Trace(EtatFne.Sending));

        var resultat = await expediteur.DebloquerAsync(Piece, null, true, confirme: false);

        Assert.False(resultat.Applique);
        Assert.True(resultat.ConfirmationManque);
        Assert.Empty(registre.Ecritures);
    }

    [Fact]
    public async Task Une_piece_absente_du_portail_redevient_a_certifier()
    {
        var (expediteur, registre) = Monter(Trace(EtatFne.Sending));

        var resultat = await expediteur.DebloquerAsync(Piece, null, true, confirme: true);

        Assert.True(resultat.Applique);
        var inscrite = Assert.Single(registre.Ecritures);
        Assert.Equal(EtatFne.Error, inscrite.Etat);
        Assert.Contains("n'y figure pas", inscrite.Erreur);

        // Et elle repart pour de bon : c'est tout l'objet du déblocage. Le
        // registre porte maintenant l'entrée classée, et une relecture doit la
        // rendre de nouveau candidate.
        var reglages = Options.Create(new FneOptions
        {
            PointOfSale = "FISH-AFRIC",
            Establishment = "FISH-AFRIC",
            Template = "B2B",
            PaymentMethod = "deferred",
        });
        var lot = await new InvoiceBatchReader(
            new DemoSageInvoiceRepository(), new FneInvoiceMapper(reglages), registre, reglages)
            .ReadAsync(InvoiceQuery.Piece(Piece));

        Assert.Equal(EtatPiece.ACertifier, lot.Conversions[0].Etat);
    }

    [Fact]
    public async Task Une_piece_trouvee_au_portail_est_classee_certifiee()
    {
        var (expediteur, registre) = Monter(Trace(EtatFne.Sending));

        var resultat = await expediteur.DebloquerAsync(
            Piece, "2304903U26000000930", false, confirme: true);

        Assert.True(resultat.Applique);
        var inscrite = Assert.Single(registre.Ecritures);
        Assert.Equal(EtatFne.Certified, inscrite.Etat);
        Assert.Equal("2304903U26000000930", inscrite.ReferenceFne);
    }

    [Fact]
    public async Task Une_certification_ne_se_reecrit_pas()
    {
        var (expediteur, registre) = Monter(Trace(EtatFne.Certified, "REF-1"));

        var resultat = await expediteur.DebloquerAsync(Piece, "REF-2", false, confirme: true);

        Assert.False(resultat.Applique);
        Assert.Empty(registre.Ecritures);
        Assert.Contains("avoir", resultat.Message);
    }

    [Fact]
    public async Task Une_piece_sans_trace_n_a_rien_a_debloquer()
    {
        var (expediteur, registre) = Monter(null);

        var resultat = await expediteur.DebloquerAsync(Piece, null, true, confirme: true);

        Assert.False(resultat.Applique);
        Assert.Empty(registre.Ecritures);
    }

    [Fact]
    public async Task Une_tentative_en_erreur_n_a_pas_besoin_d_etre_debloquee()
    {
        var (expediteur, registre) = Monter(Trace(EtatFne.Error));

        var resultat = await expediteur.DebloquerAsync(Piece, null, true, confirme: true);

        Assert.False(resultat.Applique);
        Assert.Empty(registre.Ecritures);
        Assert.Contains("rien ne la bloque", resultat.Message);
    }
}
