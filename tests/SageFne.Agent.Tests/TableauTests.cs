using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SageFne.Agent.Configuration;
using SageFne.Agent.Sante;
using SageFne.Agent.Tableau;
using SageFne.Core.Configuration;
using SageFne.Core.Data;
using SageFne.Core.Fne;
using SageFne.Core.Models.Fne;

namespace SageFne.Agent.Tests;

/// <summary>
/// Le tableau de bord : ce qu'il montre, et ce que son bouton fait.
/// </summary>
/// <remarks>
/// Tout s'éprouve à travers <see cref="RouteurTableau"/>, qui rend un objet et
/// n'ouvre aucun port. C'est ce qui permet de faire passer ici le bouton qui
/// certifie — l'endroit le plus dangereux du produit — sans dépendre d'un
/// serveur ni d'une plateforme.
/// </remarks>
public class TableauTests
{
    /// <summary>Une sonde qui répond ce qu'on lui dit, sans toucher au réseau.</summary>
    private sealed class SondeDite(bool joignable) : ISondeReseau
    {
        public Task<bool> JoignableAsync(CancellationToken cancellation = default) =>
            Task.FromResult(joignable);

        public Task<ResultatSonde> EprouverAsync(CancellationToken cancellation = default) =>
            Task.FromResult(new ResultatSonde(joignable, "essai:0", "essai"));
    }

    private static ServiceProvider Cabler(
        bool joignable = true,
        string mode = "Manual",
        params (string Cle, string Valeur)[] reglages)
    {
        // Un dictionnaire, et non une liste : AddInMemoryCollection appelle
        // Data.Add, qui lève sur une clé en double. Un test qui redéfinit un
        // réglage tombait donc pour cette raison-là, sans rapport avec ce qu'il
        // éprouve.
        var toutes = new Dictionary<string, string?>
        {
            ["Agent:Mode"] = mode,
            // Le jeu d'essai est daté de décembre 2025 : sans fenêtre large, le
            // tableau serait vide et les tests ne prouveraient rien.
            ["Agent:FenetreJours"] = "5000",
            ["Agent:StabiliteMinutes"] = "5",
            ["Fne:BaseUrl"] = "http://54.247.95.108/ws",

            // Sans elle, plus rien n'est certifiable — ce que le test dédié
            // vérifie en l'effaçant.
            ["Fne:PointOfSale"] = "POINT-ESSAI",
            ["Fne:Establishment"] = "ETAB-ESSAI",
        };

        foreach (var (cle, valeur) in reglages) toutes[cle] = valeur;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(toutes).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.Section));
        services.AjouterMiddlewareFne(configuration, chaineSage: "", cheminRegistre: null);
        services.AjouterAgent(TimeSpan.FromMilliseconds(50));

        // Après le câblage : la dernière inscription l'emporte, et la sonde
        // réelle ouvrirait un socket vers la DGI depuis un test.
        services.AddSingleton<ISondeReseau>(new SondeDite(joignable));

        return services.BuildServiceProvider();
    }

    /// <summary>Répond ce qu'on lui dit, sans rien envoyer.</summary>
    private sealed class ClientDit(FneSignResult reponse) : IFneApiClient
    {
        public bool Reel => false;
        public string DecrireRequete(FneInvoice facture) => "";

        public Task<FneSignResult> SignAsync(FneInvoice facture, CancellationToken ct = default) =>
            Task.FromResult(reponse);
    }

    /// <summary>
    /// Un conteneur qui accepte d'envoyer : jeu d'essai déclaré réel, et un
    /// client d'API qui répond ce que le test veut éprouver.
    /// </summary>
    private static ServiceProvider CablerAvecPlateforme(FneSignResult reponse) =>
        CablerAvecPlateforme(new ClientDit(reponse));

    private static ServiceProvider CablerAvecPlateforme(IFneApiClient client)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:Mode"] = "Manual",
                ["Agent:FenetreJours"] = "5000",
                ["Fne:BaseUrl"] = "http://54.247.95.108/ws",

                // Sans eux, l'expéditeur refuse avant tout appel : ils
                // identifient le contribuable auprès de la DGI et ne viennent
                // pas de Sage. Les omettre ici ferait échouer ces tests pour
                // une raison étrangère à ce qu'ils éprouvent.
                ["Fne:PointOfSale"] = "POINT-ESSAI",
                ["Fne:Establishment"] = "ETAB-ESSAI",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.Section));
        services.AjouterMiddlewareFne(configuration, chaineSage: "", cheminRegistre: null);
        services.AjouterAgent(TimeSpan.FromMilliseconds(50));

        services.AddSingleton<ISondeReseau>(new SondeDite(true));
        services.AddSingleton<ISageInvoiceRepository>(
            new DemoSageInvoiceRepository(estReel: true));
        services.AddSingleton(client);

        return services.BuildServiceProvider();
    }

    private static RouteurTableau Routeur(ServiceProvider fournisseur) =>
        fournisseur.GetRequiredService<RouteurTableau>();

    private static JsonElement Lire(ReponseHttp reponse) =>
        JsonDocument.Parse(reponse.Corps).RootElement;

    /// <summary>Le corps qu'envoie le navigateur quand un mode a été choisi.</summary>
    private const string AvecMode = "{\"modePaiement\":\"cash\"}";

    /// <summary>Certifie comme le fait l'écran : avec un mode de règlement.</summary>
    private static Task<ReponseHttp> Certifier(
        ServiceProvider fournisseur, string piece, string corps = AvecMode) =>
        Routeur(fournisseur).RepondreAsync("POST", $"/api/factures/{piece}/certifier", corps);

    // --- La page ------------------------------------------------------------

    [Fact]
    public async Task La_racine_rend_la_page()
    {
        using var fournisseur = Cabler();
        var reponse = await Routeur(fournisseur).RepondreAsync("GET", "/");

        Assert.Equal(200, reponse.Code);
        Assert.Contains("text/html", reponse.TypeContenu);
        Assert.Contains("<!doctype html>", reponse.Corps, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void La_page_ne_charge_rien_depuis_internet()
    {
        // Le poste d'un cabinet, pas un serveur. C'est précisément quand la
        // connexion tombe qu'on a besoin de voir où en sont les factures — une
        // page qui irait chercher sa feuille de style ailleurs s'afficherait
        // alors nue.
        Assert.DoesNotContain("http://", PageTableau.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", PageTableau.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/api/factures?x=1", "/api/factures")]
    [InlineData("/api/factures/", "/api/factures")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    public void Le_chemin_se_normalise(string brut, string attendu) =>
        Assert.Equal(attendu, RouteurTableau.NormaliserChemin(brut));

    [Fact]
    public async Task Une_adresse_inconnue_rend_404()
    {
        using var fournisseur = Cabler();
        Assert.Equal(404, (await Routeur(fournisseur).RepondreAsync("GET", "/nimporte")).Code);
    }

    // --- La liste -----------------------------------------------------------

    [Fact]
    public async Task La_liste_montre_les_factures_du_dossier()
    {
        using var fournisseur = Cabler();
        var reponse = await Routeur(fournisseur).RepondreAsync("GET", "/api/factures");

        Assert.Equal(200, reponse.Code);
        var lignes = Lire(reponse).EnumerateArray().ToList();

        Assert.NotEmpty(lignes);
        Assert.Contains(lignes, l => l.GetProperty("piece").GetString() == "1220");
    }

    [Fact]
    public async Task Une_piece_bloquee_dit_par_quel_controle()
    {
        // La 1222 du jeu d'essai porte un client sans NCC. Un écran qui
        // afficherait « bloquée » sans dire pourquoi n'aurait servi à rien :
        // c'est le code du contrôle qui indique quoi corriger dans Sage.
        using var fournisseur = Cabler();
        var lignes = Lire(await Routeur(fournisseur).RepondreAsync("GET", "/api/factures"))
            .EnumerateArray().ToList();

        var bloquee = lignes.Single(l => l.GetProperty("piece").GetString() == "1222");

        Assert.False(bloquee.GetProperty("certifiable").GetBoolean());
        Assert.Contains(
            bloquee.GetProperty("constats").EnumerateArray(),
            c => c.GetProperty("bloquant").GetBoolean());
    }

    [Fact]
    public async Task Le_bouton_ne_depend_pas_du_mode()
    {
        // Tout l'objet du tableau. Le mode décide de ce que l'agent fait tout
        // seul ; il ne décide pas de ce qu'un humain a le droit de demander.
        // Si « Manual » grisait le bouton, l'écran serait un rapport et non un
        // outil de travail.
        using var manuel = Cabler(mode: "Manual");
        using var automatique = Cabler(mode: "Automatic");

        static async Task<int> Certifiables(ServiceProvider f) =>
            Lire(await Routeur(f).RepondreAsync("GET", "/api/factures"))
                .EnumerateArray()
                .Count(l => l.GetProperty("certifiable").GetBoolean());

        var enManuel = await Certifiables(manuel);

        Assert.True(enManuel > 0, "Aucune pièce certifiable : le jeu d'essai ne prouve rien.");
        Assert.Equal(await Certifiables(automatique), enManuel);
    }

    [Fact]
    public async Task Le_tableau_dit_qu_il_lit_un_jeu_d_essai()
    {
        // Un écran plein de factures inventées ressemble trait pour trait à un
        // écran qui fonctionne. C'est la première chose que l'exploitant doit
        // pouvoir distinguer.
        using var fournisseur = Cabler();
        var etat = Lire(await Routeur(fournisseur).RepondreAsync("GET", "/api/etat"));

        Assert.False(etat.GetProperty("surDonneesReelles").GetBoolean());
        Assert.Equal("TEST", etat.GetProperty("environnement").GetString());
    }

    // --- Le bouton ----------------------------------------------------------

    [Fact]
    public async Task Le_tableau_annonce_une_identite_DGI_non_renseignee()
    {
        // Le paramétrage livré porte « A_COMPLETER ». La DGI refuse alors
        // TOUTES les factures — « Establishment is invalid » — et rien, ni dans
        // Sage ni dans les contrôles de pièce, ne peut le prévoir : ces deux
        // champs ne viennent pas de la facture. L'écran est le seul endroit où
        // leur absence peut se voir avant le premier refus.
        using var fournisseur = Cabler(
            reglages: [("Fne:PointOfSale", "A_COMPLETER"), ("Fne:Establishment", "A_COMPLETER")]);
        var etat = Lire(await Routeur(fournisseur).RepondreAsync("GET", "/api/etat"));

        Assert.False(etat.GetProperty("identiteRenseignee").GetBoolean());
    }

    [Fact]
    public async Task Sans_identite_DGI_aucune_facture_n_est_certifiable()
    {
        // L'écran annonçait « 4 prêtes à certifier » juste au-dessus de
        // « aucune facture ne peut être certifiée » : deux affirmations
        // contraires, sur le même écran, avec quatre boutons actifs qui
        // échouaient tous. Un bouton qui ne peut pas aboutir vaut moins que pas
        // de bouton.
        using var fournisseur = Cabler(
            reglages: [("Fne:PointOfSale", "A_COMPLETER"), ("Fne:Establishment", "A_COMPLETER")]);

        var lignes = Lire(await Routeur(fournisseur).RepondreAsync("GET", "/api/factures"))
            .EnumerateArray().ToList();
        var etat = Lire(await Routeur(fournisseur).RepondreAsync("GET", "/api/etat"));

        Assert.NotEmpty(lignes);
        Assert.All(lignes, l => Assert.False(l.GetProperty("certifiable").GetBoolean()));
        Assert.Equal(0, etat.GetProperty("certifiables").GetInt32());
    }

    [Fact]
    public async Task Une_identite_renseignee_ne_declenche_aucune_alerte()
    {
        using var fournisseur = Cabler(
            reglages: [("Fne:PointOfSale", "POINT"), ("Fne:Establishment", "ETAB")]);

        var etat = Lire(await Routeur(fournisseur).RepondreAsync("GET", "/api/etat"));

        Assert.True(etat.GetProperty("identiteRenseignee").GetBoolean());
        Assert.Equal("ETAB", etat.GetProperty("etablissement").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"modePaiement\":\"\"}")]
    [InlineData("{\"modePaiement\":\"A_COMPLETER\"}")]
    [InlineData("pas du json")]
    public async Task Sans_mode_de_reglement_rien_ne_part(string corps)
    {
        // Toutes les factures partaient jusqu'ici avec « deferred », valeur du
        // paramétrage : chaque facture certifiée déclarait « à terme », vrai ou
        // faux. La DGI marque ce champ obligatoire et Sage ne le porte pas —
        // c'est donc un choix humain, et il doit être fait.
        //
        // « virement » est le libellé français, pas le code : l'API attend
        // « transfer ». Envoyer le libellé ferait refuser la facture.
        using var fournisseur = Cabler();

        var reponse = await Certifier(fournisseur, "1220", corps);

        Assert.Equal(400, reponse.Code);
        Assert.Contains("mode de règlement", Lire(reponse).GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task Le_libelle_francais_du_portail_est_traduit_en_code()
    {
        // Le portail affiche « Virement », l'API attend « transfer ». La liste
        // déroulante transmet le code, mais un libellé recopié doit être
        // traduit plutôt que refusé — c'est déjà arrivé quatre fois avec
        // d'autres valeurs.
        using var fournisseur = CablerAvecPlateforme(
            new FneSignResult(true, 201, "REFERENCE", "JETON", "{}"));

        var reponse = await Certifier(fournisseur, "1220", "{\"modePaiement\":\"Virement\"}");

        Assert.Equal(200, reponse.Code);
    }

    [Fact]
    public async Task Le_mode_choisi_est_retenu_pour_le_client()
    {
        // Choisi facture par facture, retenu client par client : la fois
        // suivante, la liste s'ouvre déjà sur le bon mode.
        using var fournisseur = CablerAvecPlateforme(
            new FneSignResult(true, 201, "REFERENCE", "JETON", "{}"));

        await Certifier(fournisseur, "1220", "{\"modePaiement\":\"mobile-money\"}");

        var retenu = await fournisseur.GetRequiredService<IModesPaiementClients>()
            .PourAsync("4111DEMOSA");

        Assert.Equal("mobile-money", retenu);
    }

    [Fact]
    public async Task Les_six_modes_de_la_DGI_sont_servis_a_la_page()
    {
        // Servis par l'agent depuis le lexique de la DGI, jamais recopiés dans
        // la page : une liste en double finirait par diverger de ce que l'API
        // accepte.
        using var fournisseur = Cabler();
        var reponse = await Routeur(fournisseur).RepondreAsync("GET", "/api/modes-paiement");

        Assert.Equal(200, reponse.Code);
        var codes = Lire(reponse).EnumerateArray()
            .Select(m => m.GetProperty("code").GetString()).ToList();

        Assert.Equal(
            new[] { "card", "check", "cash", "mobile-money", "transfer", "deferred" }, codes);
    }

    [Fact]
    public async Task Une_piece_dit_le_mode_qui_partirait_et_s_il_a_ete_choisi()
    {
        // Un mode appliqué sans être visible est un mode qu'on découvre sur la
        // facture certifiée, quand il est trop tard pour autre chose qu'un avoir.
        using var fournisseur = Cabler();
        var lignes = Lire(await Routeur(fournisseur).RepondreAsync("GET", "/api/factures"))
            .EnumerateArray().ToList();

        var ligne = lignes.First(l => l.GetProperty("piece").GetString() == "1220");

        Assert.False(ligne.GetProperty("modePaiementChoisi").GetBoolean());
        Assert.Equal("deferred", ligne.GetProperty("modePaiement").GetString());
        Assert.Equal("À terme", ligne.GetProperty("modePaiementLibelle").GetString());
    }

    /// <summary>Retient la facture au lieu de l'envoyer.</summary>
    private sealed class ClientEspion(FneSignResult reponse) : IFneApiClient
    {
        public FneInvoice? Vue { get; private set; }
        public bool Reel => false;
        public string DecrireRequete(FneInvoice facture) => "";

        public Task<FneSignResult> SignAsync(FneInvoice facture, CancellationToken ct = default)
        {
            Vue = facture;
            return Task.FromResult(reponse);
        }
    }

    [Theory]
    [InlineData("cash", "cash")]
    [InlineData("mobile-money", "mobile-money")]
    [InlineData("Virement", "transfer")]
    [InlineData("Chèque", "check")]
    public async Task Le_mode_choisi_part_reellement_dans_le_corps_FNE(string choisi, string attendu)
    {
        // LE test qui manquait. Retirer la prise en compte du mode dans le
        // mapping ne faisait tomber aucun test : rien ne prouvait que le choix
        // de l'exploitant atteigne la DGI. Toutes les factures pouvaient
        // repartir en « deferred » sans que rien ne le dise.
        //
        // La chaîne entière est éprouvée ici : le clic, la mémoire par client,
        // la relecture du lot, le mapping, et le corps remis au client d'API.
        var espion = new ClientEspion(new FneSignResult(true, 201, "REFERENCE", "JETON", "{}"));

        using var fournisseur = CablerAvecPlateforme(espion);

        var reponse = await Certifier(
            fournisseur, "1220", "{\"modePaiement\":\"" + choisi + "\"}");

        Assert.Equal(200, reponse.Code);
        Assert.NotNull(espion.Vue);
        Assert.Equal(attendu, espion.Vue!.PaymentMethod);
    }

    [Fact]
    public async Task Sans_choix_le_corps_porte_la_valeur_du_parametrage()
    {
        // Le pendant du précédent : sans lui, un refus général passerait pour
        // une protection. L'agent en Automatic n'a personne pour choisir, et
        // doit pouvoir retomber sur le paramétrage — signalé comme supposé.
        var espion = new ClientEspion(new FneSignResult(true, 201, "REFERENCE", "JETON", "{}"));
        using var fournisseur = CablerAvecPlateforme(espion);

        var lecteur = fournisseur.GetRequiredService<SageFne.Core.Batch.InvoiceBatchReader>();
        var lot = await lecteur.ReadAsync(SageFne.Core.Data.InvoiceQuery.Piece("1220"));

        var facture = lot.Conversions.Single().Invoice;

        Assert.NotNull(facture);
        Assert.Equal("deferred", facture!.PaymentMethod);
        Assert.Contains(
            lot.Conversions.Single().Report.Constats,
            c => c.Code == "PAYMENT_METHOD_SUPPOSE");
    }

    [Fact]
    public async Task L_etat_porte_l_empreinte_du_binaire()
    {
        // Sans elle, un onglet resté ouvert pendant une republication garde
        // l'ancien code pour toujours : la page rafraîchit ses données, jamais
        // elle-même. Deux nouveautés livrées ont été crues absentes pour cette
        // seule raison, et « faites Ctrl+F5 » reporte sur l'exploitant un
        // défaut du produit.
        using var fournisseur = Cabler();
        var etat = Lire(await Routeur(fournisseur).RepondreAsync("GET", "/api/etat"));

        var build = etat.GetProperty("build").GetString();

        Assert.False(string.IsNullOrWhiteSpace(build));

        // Le numéro de version de l'assemblage vaut 1.0.0.0 et ne bougerait pas
        // d'une publication à l'autre : c'est l'identifiant de module qu'il
        // faut, et il n'y ressemble pas.
        Assert.DoesNotContain(".", build);
        Assert.Equal(12, build!.Length);
    }

    [Fact]
    public async Task L_empreinte_ne_change_pas_entre_deux_lectures()
    {
        // Elle doit changer à la republication, pas à chaque appel : une
        // empreinte instable rechargerait la page toutes les quinze secondes.
        using var fournisseur = Cabler();
        var routeur = Routeur(fournisseur);

        var premier = Lire(await routeur.RepondreAsync("GET", "/api/etat"))
            .GetProperty("build").GetString();
        var second = Lire(await routeur.RepondreAsync("GET", "/api/etat"))
            .GetProperty("build").GetString();

        Assert.Equal(premier, second);
    }

    [Fact]
    public async Task Une_visite_ne_certifie_pas()
    {
        // Une adresse qui certifierait au simple GET partirait au premier
        // préchargement du navigateur, au premier historique rouvert, au
        // premier lien cliqué par erreur.
        using var fournisseur = Cabler();

        var reponse = await Routeur(fournisseur).RepondreAsync("GET", "/api/factures/1220/certifier");

        Assert.Equal(405, reponse.Code);
    }

    [Fact]
    public async Task Plateforme_injoignable_rien_ne_part()
    {
        // La protection la plus importante du bouton. Une fois le POST parti,
        // plus rien ne distingue une coupure survenue avant de celle survenue
        // après : la pièce resterait en Sending et ne repartirait jamais seule.
        // On évite le doute plutôt que de le trancher après coup.
        using var fournisseur = Cabler(joignable: false);

        var reponse = await Certifier(fournisseur, "1220");

        Assert.Equal(503, reponse.Code);
        Assert.Contains("intacte", Lire(reponse).GetProperty("message").GetString()!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Un_numero_de_piece_vide_est_refuse(string piece)
    {
        using var fournisseur = Cabler();
        var reponse = await Certifier(fournisseur, piece);

        Assert.Equal(400, reponse.Code);
    }

    [Fact]
    public async Task Une_piece_inconnue_ne_certifie_rien()
    {
        using var fournisseur = Cabler();
        var reponse = await Certifier(fournisseur, "999999");

        Assert.Equal(422, reponse.Code);
        Assert.False(Lire(reponse).GetProperty("reussi").GetBoolean());
    }

    [Fact]
    public async Task Un_refus_rapporte_la_reponse_de_la_plateforme_mot_pour_mot()
    {
        // « la plateforme a répondu 400 Bad Request » ne dit pas ce qui cloche.
        // Le motif est dans le corps de la réponse, que le client d'API laisse
        // tomber de son message — il fallait aller le lire dans le journal. Un
        // écran qui affiche le nombre et cache la phrase fait perdre exactement
        // l'information qu'on cherche.
        const string corps = "{\"message\":\"clientNcc invalide\",\"code\":\"NCC_FORMAT\"}";

        using var fournisseur = CablerAvecPlateforme(
            new FneSignResult(false, 400, CorpsBrut: corps, Erreur: "la plateforme a répondu 400."));

        var reponse = await Certifier(fournisseur, "1220");

        var lu = Lire(reponse);
        Assert.Equal(400, lu.GetProperty("codeHttp").GetInt32());
        Assert.Equal(corps, lu.GetProperty("reponsePlateforme").GetString());
    }

    [Fact]
    public async Task Une_certification_rapporte_sa_reference()
    {
        using var fournisseur = CablerAvecPlateforme(new FneSignResult(
            true, 201, "2304903U26000000002", "JETON", "{\"reference\":\"...\"}"));

        var reponse = await Certifier(fournisseur, "1220");

        Assert.Equal(200, reponse.Code);
        var lu = Lire(reponse);
        Assert.True(lu.GetProperty("reussi").GetBoolean());
        Assert.Equal("2304903U26000000002", lu.GetProperty("referenceFne").GetString());
    }

    [Fact]
    public async Task Une_piece_bloquee_ne_part_pas_meme_si_on_la_demande()
    {
        // Le bouton passe outre la stabilité et le mode — c'est son objet. Il ne
        // passe jamais outre les contrôles métier : la 1222 n'a pas de NCC, et
        // aucun clic ne doit pouvoir l'envoyer.
        using var fournisseur = Cabler();
        var reponse = await Certifier(fournisseur, "1222");

        Assert.Equal(422, reponse.Code);
        Assert.False(Lire(reponse).GetProperty("reussi").GetBoolean());
    }
}
