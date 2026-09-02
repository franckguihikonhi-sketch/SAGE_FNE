using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SageFne.Agent.Configuration;
using SageFne.Agent.Sante;
using SageFne.Agent.Tableau;
using SageFne.Core.Configuration;

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
        var toutes = new List<KeyValuePair<string, string?>>
        {
            new("Agent:Mode", mode),
            // Le jeu d'essai est daté de décembre 2025 : sans fenêtre large, le
            // tableau serait vide et les tests ne prouveraient rien.
            new("Agent:FenetreJours", "5000"),
            new("Agent:StabiliteMinutes", "5"),
            new("Fne:BaseUrl", "http://54.247.95.108/ws"),
        };
        toutes.AddRange(reglages.Select(r => new KeyValuePair<string, string?>(r.Cle, r.Valeur)));

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

    private static RouteurTableau Routeur(ServiceProvider fournisseur) =>
        fournisseur.GetRequiredService<RouteurTableau>();

    private static JsonElement Lire(ReponseHttp reponse) =>
        JsonDocument.Parse(reponse.Corps).RootElement;

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

        var reponse = await Routeur(fournisseur).RepondreAsync("POST", "/api/factures/1220/certifier");

        Assert.Equal(503, reponse.Code);
        Assert.Contains("intacte", Lire(reponse).GetProperty("message").GetString()!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Un_numero_de_piece_vide_est_refuse(string piece)
    {
        using var fournisseur = Cabler();
        var reponse = await Routeur(fournisseur)
            .RepondreAsync("POST", $"/api/factures/{piece}/certifier");

        Assert.Equal(400, reponse.Code);
    }

    [Fact]
    public async Task Une_piece_inconnue_ne_certifie_rien()
    {
        using var fournisseur = Cabler();
        var reponse = await Routeur(fournisseur)
            .RepondreAsync("POST", "/api/factures/999999/certifier");

        Assert.Equal(422, reponse.Code);
        Assert.False(Lire(reponse).GetProperty("reussi").GetBoolean());
    }

    [Fact]
    public async Task Une_piece_bloquee_ne_part_pas_meme_si_on_la_demande()
    {
        // Le bouton passe outre la stabilité et le mode — c'est son objet. Il ne
        // passe jamais outre les contrôles métier : la 1222 n'a pas de NCC, et
        // aucun clic ne doit pouvoir l'envoyer.
        using var fournisseur = Cabler();
        var reponse = await Routeur(fournisseur)
            .RepondreAsync("POST", "/api/factures/1222/certifier");

        Assert.Equal(422, reponse.Code);
        Assert.False(Lire(reponse).GetProperty("reussi").GetBoolean());
    }
}
