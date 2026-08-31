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

    /// <summary>
    /// Ce qu'une certification doit laisser derrière elle. La liste vient du
    /// besoin réel : retrouver la facture chez la DGI, prouver quand elle est
    /// partie, et savoir si le document a bougé depuis.
    /// </summary>
    [Fact]
    public async Task Un_succes_inscrit_tout_ce_qui_permet_de_retrouver_la_facture()
    {
        var registre = new RegistreSeme();
        var client = new ClientTemoin(new FneSignResult(
            true, 200, "2304903U26000000930", "QR-TOKEN", """{"reference":"…"}"""));
        var expediteur = new InvoiceSender(
            Lecteur(registre), registre, client, NullLogger<InvoiceSender>.Instance);

        var avant = DateTimeOffset.Now;
        await expediteur.EnvoyerAsync(Piece, confirme: true);
        var apres = DateTimeOffset.Now;

        var inscrite = registre.Ecritures[^1];

        Assert.Equal(EtatFne.Certified, inscrite.Etat);
        Assert.Equal("2304903U26000000930", inscrite.ReferenceFne);
        Assert.Equal("QR-TOKEN", inscrite.Token);
        Assert.Equal(Identite, inscrite.Identite);
        Assert.Equal(Piece, inscrite.Piece);
        Assert.Equal(await EmpreinteReelle(), inscrite.Empreinte);
        Assert.InRange(inscrite.CertifieeLe, avant, apres);

        // Et la réponse brute, pour instruire après coup ce que le code n'a pas su lire.
        Assert.Equal("""{"reference":"…"}""", inscrite.Reponse);
    }

    /// <summary>
    /// Le doublon est la faute qu'on ne rattrape pas : une facture certifiée
    /// deux fois porte deux références chez la DGI.
    /// </summary>
    [Fact]
    public async Task Un_deuxieme_envoi_de_la_meme_facture_est_refuse()
    {
        var registre = new RegistreSeme(
            Trace(EtatFne.Certified, await EmpreinteReelle(), "2304903U26000000930"));
        var client = new ClientTemoin(new FneSignResult(true, 200, "AUTRE-REF"));
        var expediteur = new InvoiceSender(
            Lecteur(registre), registre, client, NullLogger<InvoiceSender>.Instance);

        var resultat = await expediteur.EnvoyerAsync(Piece, confirme: true);

        Assert.False(client.Appele, "une facture certifiée ne doit jamais repartir");
        Assert.Empty(registre.Ecritures);
        Assert.Equal(EtatFne.Error, resultat.Etat);
        Assert.Contains("déjà certifiée", resultat.Message);
    }

    /// <summary>
    /// Un envoi certifié, puis un second : le premier passe, le second est
    /// refusé par la trace que le premier a laissée. C'est la chaîne complète,
    /// et non chaque maillon pris à part.
    /// </summary>
    [Fact]
    public async Task Le_premier_envoi_bloque_le_second()
    {
        var registre = new RegistreSeme();
        var client = new ClientTemoin(new FneSignResult(true, 200, "REF-UNIQUE", "QR"));
        var expediteur = new InvoiceSender(
            Lecteur(registre), registre, client, NullLogger<InvoiceSender>.Instance);

        var premier = await expediteur.EnvoyerAsync(Piece, confirme: true);
        var ecrituresApresLePremier = registre.Ecritures.Count;

        var second = await expediteur.EnvoyerAsync(Piece, confirme: true);

        Assert.True(premier.Reussi);
        Assert.False(second.Reussi);
        Assert.Equal(ecrituresApresLePremier, registre.Ecritures.Count);
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

/// <summary>
/// Le registre sur fichier doit rendre exactement ce qu'on lui a confié : il
/// est la seule mémoire d'une certification, Sage n'en portant aucune trace.
/// </summary>
public class RegistreSurFichierTests : IDisposable
{
    private readonly string _chemin = Path.Combine(
        Path.GetTempPath(), $"registre-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_chemin)) File.Delete(_chemin);
    }

    private JsonCertificationLedger Registre() =>
        new(_chemin, NullLogger<JsonCertificationLedger>.Instance);

    [Fact]
    public async Task Une_certification_survit_a_l_ecriture_et_a_la_relecture()
    {
        var certifiee = new CertifiedInvoice
        {
            Identite = "0/6/1052",
            Piece = "1052",
            ReferenceFne = "2304903U26000000930",
            Token = "QR-TOKEN",
            CertifieeLe = new DateTimeOffset(2026, 8, 31, 14, 32, 5, TimeSpan.FromHours(0)),
            Empreinte = "bb4ac4b0c070801f4c1639c16df1040a437ae7d099aed9593bd6d22f80864a50",
            Etat = EtatFne.Certified,
            Reponse = """{"reference":"2304903U26000000930"}""",
        };

        await Registre().RecordAsync(certifiee);

        // Une seconde instance : c'est bien le fichier qui est relu, pas un cache.
        var relues = await Registre().LookupAsync(["0/6/1052"]);
        var relue = relues["0/6/1052"];

        Assert.Equal(certifiee.ReferenceFne, relue.ReferenceFne);
        Assert.Equal(certifiee.Token, relue.Token);
        Assert.Equal(certifiee.CertifieeLe, relue.CertifieeLe);
        Assert.Equal(certifiee.Identite, relue.Identite);
        Assert.Equal(certifiee.Piece, relue.Piece);
        Assert.Equal(certifiee.Empreinte, relue.Empreinte);
        Assert.Equal(EtatFne.Certified, relue.Etat);
        Assert.Equal(certifiee.Reponse, relue.Reponse);
    }

    [Fact]
    public async Task L_etat_est_ecrit_en_toutes_lettres()
    {
        // Un entier serait illisible à l'œil nu, et changerait de sens si
        // l'ordre de l'énumération bougeait.
        await Registre().RecordAsync(new CertifiedInvoice
        {
            Identite = "0/6/1052",
            Piece = "1052",
            Etat = EtatFne.Certified,
        });

        Assert.Contains("\"Certified\"", await File.ReadAllTextAsync(_chemin));
    }

    [Fact]
    public async Task Une_pièce_absente_du_registre_ne_ressort_pas()
    {
        await Registre().RecordAsync(new CertifiedInvoice { Identite = "0/6/1052", Piece = "1052" });

        var relues = await Registre().LookupAsync(["0/6/1053"]);

        Assert.Empty(relues);
    }

    [Fact]
    public async Task Une_seconde_inscription_remplace_la_premiere_sans_la_dupliquer()
    {
        var registre = Registre();
        var enCours = new CertifiedInvoice
        {
            Identite = "0/6/1052",
            Piece = "1052",
            Etat = EtatFne.Sending,
        };

        await registre.RecordAsync(enCours);
        await registre.RecordAsync(enCours with { Etat = EtatFne.Certified, ReferenceFne = "REF" });

        var relues = await Registre().LookupAsync(["0/6/1052"]);

        Assert.Single(relues);
        Assert.Equal(EtatFne.Certified, relues["0/6/1052"].Etat);
        Assert.Equal("REF", relues["0/6/1052"].ReferenceFne);
    }
}
