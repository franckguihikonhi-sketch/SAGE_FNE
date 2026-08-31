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

/// <summary>
/// Un registre illisible n'est pas un registre vide.
/// </summary>
/// <remarks>
/// C'était le défaut le plus dangereux du lot : un fichier tronqué était lu
/// comme « aucune pièce certifiée », et toutes les factures déjà certifiées
/// redevenaient envoyables. Il ne s'est vu qu'après la disparition d'une trace
/// réelle.
/// </remarks>
public class RegistreIllisibleTests : IDisposable
{
    private readonly string _chemin = Path.Combine(
        Path.GetTempPath(), $"registre-casse-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_chemin)) File.Delete(_chemin);
    }

    private JsonCertificationLedger Registre() =>
        new(_chemin, NullLogger<JsonCertificationLedger>.Instance);

    [Theory]
    [InlineData("""[{"identite":"0/6/1052","piece":""")]
    [InlineData("pas du json du tout")]
    [InlineData("{")]
    public async Task Un_fichier_tronque_leve_au_lieu_de_passer_pour_vide(string contenu)
    {
        await File.WriteAllTextAsync(_chemin, contenu);

        var erreur = await Assert.ThrowsAsync<RegistreIllisibleException>(
            () => Registre().LookupAsync(["0/6/1052"]));

        Assert.Equal(_chemin, erreur.Chemin);
        Assert.Contains("illisible", erreur.Message);
    }

    [Fact]
    public async Task Un_lot_ne_se_juge_pas_sur_un_registre_illisible()
    {
        // La conséquence qui compte : sans cela, le lot annoncerait « à
        // certifier » des pièces que la DGI a déjà certifiées.
        await File.WriteAllTextAsync(_chemin, "{");
        var reglages = Options.Create(new FneOptions
        {
            PointOfSale = "FISH-AFRIC",
            Establishment = "FISH-AFRIC",
            Template = "B2B",
            PaymentMethod = "deferred",
        });
        var lecteur = new InvoiceBatchReader(
            new DemoSageInvoiceRepository(), new FneInvoiceMapper(reglages), Registre(), reglages);

        await Assert.ThrowsAsync<RegistreIllisibleException>(
            () => lecteur.ReadAsync(InvoiceQuery.Piece("1221")));
    }

    [Fact]
    public async Task Le_diagnostic_decrit_un_registre_illisible_sans_lever()
    {
        // C'est la commande qu'on lance quand tout le reste refuse : elle doit
        // répondre, pas échouer à son tour.
        await File.WriteAllTextAsync(_chemin, "{");

        var etat = await Registre().EtatDuFichierAsync();

        Assert.True(etat.Existe);
        Assert.NotNull(etat.Illisible);
        Assert.Null(etat.Entrees);
        Assert.True(etat.Octets > 0);
        Assert.Equal(Path.GetFullPath(_chemin), etat.Chemin);
    }

    [Fact]
    public async Task Le_diagnostic_compte_les_entrees_d_un_registre_sain()
    {
        await Registre().RecordAsync(new CertifiedInvoice
        {
            Identite = "0/6/1052",
            Piece = "1052",
            ReferenceFne = "REF",
            Etat = EtatFne.Certified,
        });

        var etat = await Registre().EtatDuFichierAsync();

        Assert.True(etat.Existe);
        Assert.Null(etat.Illisible);
        Assert.Equal("1052", Assert.Single(etat.Entrees!).Piece);
    }

    [Fact]
    public async Task Un_registre_absent_n_est_pas_un_registre_illisible()
    {
        // Rien n'a encore été inscrit : c'est un état normal, pas une panne.
        var etat = await Registre().EtatDuFichierAsync();

        Assert.False(etat.Existe);
        Assert.Null(etat.Illisible);
        Assert.Empty(await Registre().LookupAsync(["0/6/1052"]));
    }
}

/// <summary>
/// Le registre par défaut ne doit pas vivre dans une sortie de compilation.
/// </summary>
public class CheminDurableTests
{
    [Fact]
    public void Le_registre_par_defaut_ne_vit_plus_dans_bin()
    {
        // La trace d'une certification réelle a disparu parce que le registre
        // était posé dans bin\Debug\net8.0\, que dotnet clean efface.
        var chemin = ServicesMiddleware.CheminRegistre(
            null, null, "/app/bin/Debug/net8.0", connexionSageConfiguree: true);

        Assert.NotNull(chemin);
        Assert.DoesNotContain("bin", chemin);
        Assert.DoesNotContain("Debug", chemin);
        Assert.EndsWith("certifications.json", chemin);
    }

    [Fact]
    public void Le_chemin_durable_est_absolu()
    {
        Assert.True(Path.IsPathRooted(ServicesMiddleware.CheminDurable()));
    }

    [Fact]
    public void L_ancien_emplacement_reste_calculable_pour_le_diagnostic()
    {
        // Un registre écrit avant le changement s'y trouve encore : le
        // diagnostic doit pouvoir aller le montrer.
        Assert.Equal(
            Path.Combine("/app/bin/Debug/net8.0", "certifications.json"),
            ServicesMiddleware.AncienChemin("/app/bin/Debug/net8.0"));
    }

    [Fact]
    public void Un_chemin_explicite_l_emporte_toujours()
    {
        Assert.Equal("/data/reg.json",
            ServicesMiddleware.CheminRegistre(null, "/data/reg.json", "/app", true));
        Assert.Equal("/autre/reg.json",
            ServicesMiddleware.CheminRegistre("/autre/reg.json", "/data/reg.json", "/app", true));
    }
}

/// <summary>
/// Rattraper une certification dont la trace manque, sans rien inventer.
/// </summary>
public class ReconciliationTests
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
            throw new InvalidOperationException("reconcilier ne doit appeler aucune API");
    }

    private const string Piece = "1221";
    private const string Reference = "2304903U26000000930";

    private static (InvoiceSender Expediteur, Registre Registre) Monter(CertifiedInvoice? entree = null)
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
    public async Task Sans_reference_rien_ne_s_inscrit()
    {
        var (expediteur, registre) = Monter();

        var resultat = await expediteur.ReconcilierAsync(Piece, null, null, confirme: true);

        Assert.False(resultat.Applique);
        Assert.Empty(registre.Ecritures);
        Assert.Contains("--reference", resultat.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Une_reference_vide_ne_vaut_pas_reference(string reference)
    {
        var (expediteur, registre) = Monter();

        var resultat = await expediteur.ReconcilierAsync(Piece, reference, null, confirme: true);

        Assert.False(resultat.Applique);
        Assert.Empty(registre.Ecritures);
    }

    [Fact]
    public async Task Sans_confirmation_rien_ne_s_inscrit_mais_tout_est_montre()
    {
        var (expediteur, registre) = Monter();

        var resultat = await expediteur.ReconcilierAsync(Piece, Reference, "QR", confirme: false);

        Assert.False(resultat.Applique);
        Assert.True(resultat.ConfirmationManque);
        Assert.Empty(registre.Ecritures);
        Assert.Contains(Reference, resultat.Message);
        Assert.Contains("0/6/1221", resultat.Message);
        Assert.Contains("Réconciliation manuelle", resultat.Message);
    }

    [Fact]
    public async Task La_reconciliation_inscrit_tout_ce_qu_il_faut()
    {
        var (expediteur, registre) = Monter();

        var resultat = await expediteur.ReconcilierAsync(Piece, Reference, "QR-1052", confirme: true);

        Assert.True(resultat.Applique);
        var inscrite = Assert.Single(registre.Ecritures);
        Assert.Equal(EtatFne.Certified, inscrite.Etat);
        Assert.Equal(Reference, inscrite.ReferenceFne);
        Assert.Equal("QR-1052", inscrite.Token);
        Assert.Equal("0/6/1221", inscrite.Identite);
        Assert.Equal(Piece, inscrite.Piece);
        Assert.NotEqual("", inscrite.Empreinte);
        Assert.Equal(SourceCertification.ReconciliationManuelle, inscrite.Source);
        Assert.Contains("Réconciliation manuelle", inscrite.Motif);
    }

    [Fact]
    public async Task Le_jeton_est_facultatif()
    {
        // Tous les PDF ne le portent pas ; l'absence ne doit pas bloquer.
        var (expediteur, registre) = Monter();

        var resultat = await expediteur.ReconcilierAsync(Piece, Reference, null, confirme: true);

        Assert.True(resultat.Applique);
        Assert.Equal("", registre.Ecritures[^1].Token);
    }

    [Fact]
    public async Task Une_reconciliation_empeche_tout_renvoi()
    {
        // Sa raison d'être : la pièce ne doit plus jamais partir.
        var (expediteur, registre) = Monter();

        await expediteur.ReconcilierAsync(Piece, Reference, "QR", confirme: true);
        var apresReconciliation = registre.Ecritures.Count;
        var envoi = await expediteur.EnvoyerAsync(Piece, confirme: true);

        Assert.False(envoi.Reussi);
        Assert.Contains("déjà certifiée", envoi.Message);
        Assert.Equal(apresReconciliation, registre.Ecritures.Count);
    }

    [Fact]
    public async Task Une_certification_existante_ne_se_reecrit_pas()
    {
        var (expediteur, registre) = Monter(new CertifiedInvoice
        {
            Identite = "0/6/1221",
            Piece = Piece,
            ReferenceFne = "REF-ORIGINE",
            Etat = EtatFne.Certified,
            Empreinte = "peu importe",
        });

        var resultat = await expediteur.ReconcilierAsync(Piece, "REF-AUTRE", null, confirme: true);

        Assert.False(resultat.Applique);
        Assert.Empty(registre.Ecritures);
        Assert.Contains("REF-ORIGINE", resultat.Message);
    }

    [Fact]
    public async Task Une_tentative_en_erreur_se_reconcilie()
    {
        // Le cas réel : un envoi refusé, puis une certification obtenue
        // autrement. La trace d'échec ne doit pas empêcher le rattrapage.
        var (expediteur, registre) = Monter(new CertifiedInvoice
        {
            Identite = "0/6/1221",
            Piece = Piece,
            Etat = EtatFne.Error,
            Empreinte = "peu importe",
        });

        var resultat = await expediteur.ReconcilierAsync(Piece, Reference, null, confirme: true);

        Assert.True(resultat.Applique);
        Assert.Equal(EtatFne.Certified, registre.Ecritures[^1].Etat);
    }

    [Fact]
    public async Task Une_piece_absente_de_Sage_ne_se_reconcilie_pas()
    {
        var (expediteur, registre) = Monter();

        var resultat = await expediteur.ReconcilierAsync("999999", Reference, null, confirme: true);

        Assert.False(resultat.Applique);
        Assert.Empty(registre.Ecritures);
    }

    [Fact]
    public async Task Une_piece_que_nos_controles_bloquent_se_reconcilie_quand_meme()
    {
        // 1222 n'a pas de NCC : nos contrôles la refusent à l'envoi. Mais si la
        // DGI l'a certifiée — c'est ce que l'exploitant atteste — le refuser
        // laisserait la pièce envoyable, ce qui est exactement le danger. La
        // réalité constatée l'emporte sur notre opinion du document.
        var (expediteur, registre) = Monter();

        var resultat = await expediteur.ReconcilierAsync("1222", Reference, null, confirme: true);

        Assert.True(resultat.Applique);
        Assert.Equal(EtatFne.Certified, registre.Ecritures[^1].Etat);
    }
}

/// <summary>
/// Une certification sans référence en est une tout autant.
/// </summary>
/// <remarks>
/// La plateforme d'essai de la DGI certifie des factures sans publier de
/// référence exploitable. Exiger un numéro poussait à en inventer un — c'est
/// arrivé, une valeur d'exemple ayant été recopiée telle quelle. Une référence
/// inventée est pire que pas de référence : elle désigne chez la DGI une
/// facture qui n'existe pas.
/// </remarks>
public class CertificationSansReferenceTests
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

    private sealed class ClientTemoin : IFneApiClient
    {
        public bool Appele { get; private set; }
        public bool Reel => false;
        public string DecrireRequete(FneInvoice facture) => "";

        public Task<FneSignResult> SignAsync(FneInvoice facture, CancellationToken ct = default)
        {
            Appele = true;
            return Task.FromResult(new FneSignResult(true, 200, "NOUVELLE-REF"));
        }
    }

    private const string Piece = "1221";
    private const string Identite = "0/6/1221";
    private const string Placeholder = "TA_REFERENCE_FNE";

    private static IOptions<FneOptions> Reglages() => Options.Create(new FneOptions
    {
        PointOfSale = "FISH-AFRIC",
        Establishment = "FISH-AFRIC",
        Template = "B2B",
        PaymentMethod = "deferred",
    });

    private static (InvoiceSender Expediteur, Registre Registre, ClientTemoin Client, InvoiceBatchReader Lecteur)
        Monter(CertifiedInvoice? entree = null)
    {
        var registre = new Registre(entree);
        var client = new ClientTemoin();
        var reglages = Reglages();
        var lecteur = new InvoiceBatchReader(
            new DemoSageInvoiceRepository(), new FneInvoiceMapper(reglages), registre, reglages);

        return (new InvoiceSender(lecteur, registre, client, NullLogger<InvoiceSender>.Instance),
            registre, client, lecteur);
    }

    private static CertifiedInvoice Certifiee(
        string reference = "",
        string token = "",
        SourceCertification source = SourceCertification.ReconciliationManuelle,
        string empreinte = "peu importe") => new()
    {
        Identite = Identite,
        Piece = Piece,
        ReferenceFne = reference,
        Token = token,
        Empreinte = empreinte,
        Etat = EtatFne.Certified,
        Source = source,
        CertifieeLe = new DateTimeOffset(2026, 8, 31, 14, 32, 5, TimeSpan.Zero),
        Motif = "Constat de portail.",
    };

    // --- Réconcilier sans référence -----------------------------------------

    [Fact]
    public async Task Une_reconciliation_sans_reference_exige_qu_on_le_dise()
    {
        // Sans le constat explicite, une faute de frappe passerait pour lui.
        var (expediteur, registre, _, _) = Monter();

        var resultat = await expediteur.ReconcilierAsync(Piece, null, null, confirme: true);

        Assert.False(resultat.Applique);
        Assert.Empty(registre.Ecritures);
        Assert.Contains("--sans-reference", resultat.Message);
    }

    [Fact]
    public async Task Les_deux_constats_a_la_fois_sont_refuses()
    {
        var (expediteur, registre, _, _) = Monter();

        var resultat = await expediteur.ReconcilierAsync(
            Piece, "REF", null, confirme: true, sansReference: true, motif: "m");

        Assert.False(resultat.Applique);
        Assert.Empty(registre.Ecritures);
    }

    [Fact]
    public async Task Une_reconciliation_sans_reference_exige_un_motif()
    {
        var (expediteur, registre, _, _) = Monter();

        var resultat = await expediteur.ReconcilierAsync(
            Piece, null, null, confirme: true, sansReference: true);

        Assert.False(resultat.Applique);
        Assert.Empty(registre.Ecritures);
        Assert.Contains("--motif", resultat.Message);
    }

    [Fact]
    public async Task Une_certification_sans_reference_ni_jeton_s_inscrit()
    {
        var (expediteur, registre, _, _) = Monter();

        var resultat = await expediteur.ReconcilierAsync(
            Piece, null, null, confirme: true, sansReference: true,
            motif: "Aucune référence FNE visible sur le portail/PDF TEST");

        Assert.True(resultat.Applique);
        var inscrite = Assert.Single(registre.Ecritures);
        Assert.Equal(EtatFne.Certified, inscrite.Etat);
        Assert.True(inscrite.SansReference);
        Assert.Equal("", inscrite.Token);
        Assert.Equal(SourceCertification.ReconciliationManuelle, inscrite.Source);
        Assert.Contains("portail/PDF TEST", inscrite.Motif);
    }

    [Fact]
    public async Task Une_certification_sans_reference_bloque_le_renvoi()
    {
        // Le point qui compte : l'absence de numéro ne rend pas la pièce
        // envoyable. C'est l'identité Sage qui fait foi, jamais la référence.
        var (expediteur, registre, client, _) = Monter(Certifiee(empreinte: await EmpreinteReelle()));

        var resultat = await expediteur.EnvoyerAsync(Piece, confirme: true);

        Assert.False(client.Appele, "une pièce certifiée ne repart pas, référence ou non");
        Assert.Empty(registre.Ecritures);
        Assert.Contains("déjà certifiée", resultat.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("empreinte-d-avant")]
    public async Task Aucun_etat_de_certification_sans_reference_n_est_envoyable(string empreinte)
    {
        // Empreinte concordante ou non, la pièce reste bloquée : « déjà
        // certifiée » d'un côté, « modifiée depuis » de l'autre.
        var (_, _, _, lecteur) = Monter(Certifiee(empreinte: empreinte));

        var lot = await lecteur.ReadAsync(InvoiceQuery.Piece(Piece));

        Assert.NotEqual(EtatPiece.ACertifier, lot.Conversions[0].Etat);
    }

    private static async Task<string> EmpreinteReelle()
    {
        var reglages = Reglages();
        var lot = await new InvoiceBatchReader(
            new DemoSageInvoiceRepository(), new FneInvoiceMapper(reglages),
            new Registre(null), reglages).ReadAsync(InvoiceQuery.Piece(Piece));
        return lot.Conversions[0].Empreinte;
    }

    // --- Corriger une référence fautive -------------------------------------

    [Fact]
    public async Task La_correction_exige_un_motif()
    {
        var (expediteur, registre, _, _) = Monter(Certifiee(Placeholder));

        var resultat = await expediteur.CorrigerReferenceAsync(
            Piece, Placeholder, null, supprimerJeton: false, confirme: true);

        Assert.False(resultat.Applique);
        Assert.Empty(registre.Ecritures);
        Assert.Contains("--motif", resultat.Message);
    }

    [Fact]
    public async Task La_correction_exige_la_reference_attendue()
    {
        var (expediteur, registre, _, _) = Monter(Certifiee(Placeholder));

        var resultat = await expediteur.CorrigerReferenceAsync(
            Piece, null, "motif", supprimerJeton: false, confirme: true);

        Assert.False(resultat.Applique);
        Assert.Empty(registre.Ecritures);
        Assert.Contains("--reference-actuelle", resultat.Message);
    }

    [Fact]
    public async Task La_correction_refuse_si_le_registre_porte_autre_chose()
    {
        // Le verrou : le registre a pu changer depuis qu'on l'a lu.
        var (expediteur, registre, _, _) = Monter(Certifiee("UNE_VRAIE_REFERENCE"));

        var resultat = await expediteur.CorrigerReferenceAsync(
            Piece, Placeholder, "motif", supprimerJeton: false, confirme: true);

        Assert.False(resultat.Applique);
        Assert.Empty(registre.Ecritures);
        Assert.Contains("UNE_VRAIE_REFERENCE", resultat.Message);
    }

    [Fact]
    public async Task Sans_confirmation_la_correction_montre_et_n_ecrit_rien()
    {
        var (expediteur, registre, _, _) = Monter(Certifiee(Placeholder));

        var resultat = await expediteur.CorrigerReferenceAsync(
            Piece, Placeholder, "Aucune référence au portail", supprimerJeton: false, confirme: false);

        Assert.False(resultat.Applique);
        Assert.True(resultat.ConfirmationManque);
        Assert.Empty(registre.Ecritures);
        Assert.Contains(Placeholder, resultat.Message);
        Assert.Contains("Certified", resultat.Message);
    }

    [Fact]
    public async Task La_correction_retire_la_reference_et_conserve_tout_le_reste()
    {
        var origine = Certifiee(Placeholder, empreinte: "empreinte-d-origine");
        var (expediteur, registre, _, _) = Monter(origine);

        var resultat = await expediteur.CorrigerReferenceAsync(
            Piece, Placeholder, "Aucune référence FNE visible sur le portail/PDF TEST",
            supprimerJeton: false, confirme: true);

        Assert.True(resultat.Applique);
        var corrigee = Assert.Single(registre.Ecritures);

        Assert.True(corrigee.SansReference);
        Assert.Equal(EtatFne.Certified, corrigee.Etat);
        Assert.Equal(Identite, corrigee.Identite);
        Assert.Equal("empreinte-d-origine", corrigee.Empreinte);
        Assert.Equal(origine.CertifieeLe, corrigee.CertifieeLe);

        // Le motif d'origine survit à la correction : le registre n'efface pas
        // son passé.
        Assert.Contains("Constat de portail.", corrigee.Motif);
        Assert.Contains("portail/PDF TEST", corrigee.Motif);
        Assert.Contains(Placeholder, corrigee.Motif);
    }

    [Fact]
    public async Task Le_jeton_ne_part_que_si_on_le_demande()
    {
        var (expediteur, registre, _, _) = Monter(Certifiee(Placeholder, token: "FAUX-JETON"));

        await expediteur.CorrigerReferenceAsync(
            Piece, Placeholder, "motif", supprimerJeton: false, confirme: true);

        Assert.Equal("FAUX-JETON", registre.Ecritures[^1].Token);
    }

    [Fact]
    public async Task Le_jeton_part_quand_on_le_demande()
    {
        var (expediteur, registre, _, _) = Monter(Certifiee(Placeholder, token: "FAUX-JETON"));

        await expediteur.CorrigerReferenceAsync(
            Piece, Placeholder, "motif", supprimerJeton: true, confirme: true);

        Assert.Equal("", registre.Ecritures[^1].Token);
    }

    [Fact]
    public async Task Apres_correction_la_piece_reste_bloquee()
    {
        // Toute la raison d'être de la commande : corriger sans jamais rendre
        // la facture renvoyable.
        var (expediteur, registre, client, lecteur) =
            Monter(Certifiee(Placeholder, empreinte: await EmpreinteReelle()));

        await expediteur.CorrigerReferenceAsync(
            Piece, Placeholder, "Aucune référence au portail", supprimerJeton: true, confirme: true);
        var ecrituresApresCorrection = registre.Ecritures.Count;

        var lot = await lecteur.ReadAsync(InvoiceQuery.Piece(Piece));
        var envoi = await expediteur.EnvoyerAsync(Piece, confirme: true);

        Assert.Equal(EtatPiece.DejaCertifiee, lot.Conversions[0].Etat);
        Assert.False(client.Appele);
        Assert.False(envoi.Reussi);
        Assert.Equal(ecrituresApresCorrection, registre.Ecritures.Count);
    }

    [Fact]
    public async Task Une_reference_venue_de_la_DGI_ne_se_retire_pas()
    {
        // Elle n'est pas une déclaration humaine : elle fait foi.
        var (expediteur, registre, _, _) = Monter(
            Certifiee("REF-DGI", source: SourceCertification.Middleware));

        var resultat = await expediteur.CorrigerReferenceAsync(
            Piece, "REF-DGI", "motif", supprimerJeton: false, confirme: true);

        Assert.False(resultat.Applique);
        Assert.Empty(registre.Ecritures);
        Assert.Contains("fait foi", resultat.Message);
    }

    [Fact]
    public async Task Une_piece_non_certifiee_ne_se_corrige_pas()
    {
        var (expediteur, registre, _, _) = Monter(Certifiee(Placeholder) with { Etat = EtatFne.Error });

        var resultat = await expediteur.CorrigerReferenceAsync(
            Piece, Placeholder, "motif", supprimerJeton: false, confirme: true);

        Assert.False(resultat.Applique);
        Assert.Empty(registre.Ecritures);
    }

    [Fact]
    public async Task Une_piece_sans_trace_ne_se_corrige_pas()
    {
        var (expediteur, registre, _, _) = Monter();

        var resultat = await expediteur.CorrigerReferenceAsync(
            Piece, Placeholder, "motif", supprimerJeton: false, confirme: true);

        Assert.False(resultat.Applique);
        Assert.Empty(registre.Ecritures);
    }
}

/// <summary>
/// Une correction porte sur la seule mémoire des certifications : elle se
/// sauvegarde avant de s'appliquer.
/// </summary>
public class SauvegardeDuRegistreTests : IDisposable
{
    private readonly string _dossier = Path.Combine(
        Path.GetTempPath(), $"registre-{Guid.NewGuid():N}");

    private string Chemin => Path.Combine(_dossier, "certifications.json");

    public SauvegardeDuRegistreTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        if (Directory.Exists(_dossier)) Directory.Delete(_dossier, recursive: true);
    }

    private JsonCertificationLedger Registre() =>
        new(Chemin, NullLogger<JsonCertificationLedger>.Instance);

    private string[] Sauvegardes() => Directory.GetFiles(_dossier, "*.sauvegarde");

    [Fact]
    public async Task Une_correction_cree_une_sauvegarde()
    {
        var registre = Registre();
        await registre.RecordAsync(new CertifiedInvoice
        {
            Identite = "0/6/1221",
            Piece = "1221",
            ReferenceFne = "TA_REFERENCE_FNE",
            Etat = EtatFne.Certified,
            Source = SourceCertification.ReconciliationManuelle,
            Empreinte = "e",
        });

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
            lecteur, registre, new ClientMuet(), NullLogger<InvoiceSender>.Instance);

        Assert.Empty(Sauvegardes());

        var resultat = await expediteur.CorrigerReferenceAsync(
            "1221", "TA_REFERENCE_FNE", "Aucune référence au portail",
            supprimerJeton: true, confirme: true);

        Assert.True(resultat.Applique);
        var copie = Assert.Single(Sauvegardes());

        // La copie porte l'état d'AVANT : c'est tout son intérêt.
        Assert.Contains("TA_REFERENCE_FNE", await File.ReadAllTextAsync(copie));

        // Le registre corrigé ne la porte plus comme référence — mais le motif
        // la nomme encore, et c'est voulu : la trace doit dire ce qui a été
        // retiré, sans quoi la correction serait indéchiffrable.
        var apresCorrection = await Registre().LookupAsync(["0/6/1221"]);
        var ligne = apresCorrection["0/6/1221"];
        Assert.True(ligne.SansReference);
        Assert.Equal(EtatFne.Certified, ligne.Etat);
        Assert.Contains("TA_REFERENCE_FNE", ligne.Motif);
    }

    [Fact]
    public async Task Un_apercu_ne_sauvegarde_rien()
    {
        var registre = Registre();
        await registre.RecordAsync(new CertifiedInvoice
        {
            Identite = "0/6/1221",
            Piece = "1221",
            ReferenceFne = "TA_REFERENCE_FNE",
            Etat = EtatFne.Certified,
            Source = SourceCertification.ReconciliationManuelle,
            Empreinte = "e",
        });

        var reglages = Options.Create(new FneOptions
        {
            PointOfSale = "FISH-AFRIC",
            Establishment = "FISH-AFRIC",
            Template = "B2B",
            PaymentMethod = "deferred",
        });
        var lecteur = new InvoiceBatchReader(
            new DemoSageInvoiceRepository(), new FneInvoiceMapper(reglages), registre, reglages);

        await new InvoiceSender(lecteur, registre, new ClientMuet(), NullLogger<InvoiceSender>.Instance)
            .CorrigerReferenceAsync("1221", "TA_REFERENCE_FNE", "motif", false, confirme: false);

        Assert.Empty(Sauvegardes());
    }

    [Fact]
    public async Task Deux_sauvegardes_rapprochees_ne_s_ecrasent_pas()
    {
        var registre = Registre();
        await registre.RecordAsync(new CertifiedInvoice { Identite = "0/6/1", Piece = "1" });

        await registre.SauvegarderAsync();
        await registre.SauvegarderAsync();

        Assert.Equal(2, Sauvegardes().Length);
    }

    [Fact]
    public async Task Un_registre_absent_n_a_rien_a_sauvegarder()
    {
        Assert.Null(await Registre().SauvegarderAsync());
    }

    private sealed class ClientMuet : IFneApiClient
    {
        public bool Reel => false;
        public string DecrireRequete(FneInvoice facture) => "";

        public Task<FneSignResult> SignAsync(FneInvoice facture, CancellationToken ct = default) =>
            throw new InvalidOperationException("aucune API ne doit être appelée");
    }
}
