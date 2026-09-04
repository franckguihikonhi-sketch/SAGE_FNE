using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SageFne.Agent.Certification;
using SageFne.Agent.Saas;
using SageFne.Core.Models.Sage;
using SageFne.Core.Saas;

namespace SageFne.Agent.Tests;

/// <summary>
/// Les clics venus de l'écran distant.
/// </summary>
/// <remarks>
/// Une demande dit « quelqu'un a cliqué », jamais « certifie ». Ce que ces
/// tests tiennent, c'est qu'elle ne puisse pas devenir un ordre : ni court-
/// circuiter les contrôles, ni être rejouée après un arrêt, ni faire partir un
/// mode de règlement que la DGI ne connaît pas.
/// </remarks>
public class DemandesSaasTests
{
    private sealed class DemandesFeintes(params DemandeSaas[] demandes) : IDemandesClient
    {
        public bool Actif { get; init; } = true;
        public List<string> Prises { get; } = [];
        public List<(string Id, bool Reussi, string Resultat)> Verdicts { get; } = [];
        public HashSet<string> Refuse { get; } = [];

        public Task<IReadOnlyList<DemandeSaas>> EnAttenteAsync(int limite, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DemandeSaas>>([.. demandes.Take(limite)]);

        public Task<bool> PrendreAsync(string id, CancellationToken ct = default)
        {
            if (Refuse.Contains(id)) return Task.FromResult(false);
            Prises.Add(id);
            return Task.FromResult(true);
        }

        public Task TrancherAsync(string id, bool reussi, string resultat, CancellationToken ct = default)
        {
            Verdicts.Add((id, reussi, resultat));
            return Task.CompletedTask;
        }
    }

    /// <summary>Note ce qu'on lui demande de certifier, sans rien envoyer.</summary>
    private sealed class CertificateurEspion(IssueCertification issue) : ICertificateur
    {
        public List<(string Piece, string Mode, short Domaine, string Origine)> Appels { get; } = [];

        public bool EnCours(string piece) => false;

        public Task<IssueCertification> CertifierAsync(
            string piece, string mode, short domaine, string origine, CancellationToken ct = default)
        {
            Appels.Add((piece, mode, domaine, origine));
            return Task.FromResult(issue);
        }
    }

    private static DemandeSaas Demande(
        string id = "d1", string identite = "0/6/1225",
        string piece = "1225", string mode = "cash") =>
        new(id, identite, piece, mode);

    // --- L'identité porte le domaine ----------------------------------------

    [Theory]
    [InlineData("0/6/1225", SageDomaines.Vente)]
    [InlineData("1/16/358", SageDomaines.Achat)]
    public void Le_domaine_se_lit_dans_l_identite(string identite, short attendu) =>
        Assert.Equal(attendu, SageDomaines.DepuisIdentite(identite));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("sans-separateur")]
    [InlineData("x/6/1225")]
    public void Une_identite_illisible_ne_devient_jamais_un_achat(string? identite)
    {
        // Deviner l'achat serait plus grave que de se tromper : l'achat mène au
        // bordereau agricole, qui est une affirmation devant la DGI.
        Assert.Equal(SageDomaines.Vente, SageDomaines.DepuisIdentite(identite));
    }

    // --- Ce que le traiteur fait, et dans quel ordre -------------------------

    [Fact]
    public async Task Eteint_rien_n_est_lu_ni_pris()
    {
        var demandes = new DemandesFeintes(Demande()) { Actif = false };
        var traiteur = new TraiteurDemandes(
            demandes, Certificateur(), NullLogger<TraiteurDemandes>.Instance);

        Assert.Equal(0, await traiteur.TraiterAsync(10));
        Assert.Empty(demandes.Prises);
    }

    [Fact]
    public async Task Une_demande_deja_prise_ailleurs_est_laissee()
    {
        // C'est PostgreSQL qui départage, pas un verrou en mémoire : deux
        // agents sur le même dossier ne peuvent pas envoyer la même facture.
        var demandes = new DemandesFeintes(Demande());
        demandes.Refuse.Add("d1");

        var traiteur = new TraiteurDemandes(
            demandes, Certificateur(), NullLogger<TraiteurDemandes>.Instance);

        Assert.Equal(0, await traiteur.TraiterAsync(10));
        Assert.Empty(demandes.Verdicts);
    }

    [Fact]
    public async Task Un_mode_de_reglement_inconnu_est_refuse_sans_rien_envoyer()
    {
        // La base contraint déjà les six codes ; un schéma plus récent que
        // l'agent pourrait en porter un de plus. Refuser vaut mieux qu'envoyer
        // à la DGI un mode qu'elle ne connaît pas.
        var demandes = new DemandesFeintes(Demande(mode: "bitcoin"));
        var traiteur = new TraiteurDemandes(
            demandes, Certificateur(), NullLogger<TraiteurDemandes>.Instance);

        await traiteur.TraiterAsync(10);

        var verdict = Assert.Single(demandes.Verdicts);
        Assert.False(verdict.Reussi);
        Assert.Contains("bitcoin", verdict.Resultat);
        Assert.Contains("Rien n'a été envoyé", verdict.Resultat);
    }

    [Fact]
    public async Task Une_demande_est_prise_AVANT_d_agir()
    {
        // L'ordre est la seule protection contre le rejeu. Si la machine
        // s'arrête entre les deux, la demande reste « prise » et ne repartira
        // jamais toute seule : une demande bloquée se voit et se règle à la
        // main, une demande rejouée fabrique un doublon.
        var demandes = new DemandesFeintes(Demande());
        var espion = Certificateur();

        await new TraiteurDemandes(demandes, espion, NullLogger<TraiteurDemandes>.Instance)
            .TraiterAsync(10);

        Assert.Equal("d1", Assert.Single(demandes.Prises));
        Assert.Single(espion.Appels);
    }

    [Fact]
    public async Task Le_domaine_envoye_vient_de_l_identite_de_la_demande()
    {
        var demandes = new DemandesFeintes(Demande(identite: "1/16/358", piece: "358"));
        var espion = Certificateur();

        await new TraiteurDemandes(demandes, espion, NullLogger<TraiteurDemandes>.Instance)
            .TraiterAsync(10);

        Assert.Equal(SageDomaines.Achat, Assert.Single(espion.Appels).Domaine);
    }

    [Fact]
    public async Task Un_libelle_francais_est_traduit_avant_de_partir()
    {
        // Le portail affiche « Virement », l'API attend « transfer ».
        var demandes = new DemandesFeintes(Demande(mode: "Virement"));
        var espion = Certificateur();

        await new TraiteurDemandes(demandes, espion, NullLogger<TraiteurDemandes>.Instance)
            .TraiterAsync(10);

        Assert.Equal("transfer", Assert.Single(espion.Appels).Mode);
    }

    [Fact]
    public async Task Le_verdict_reprend_la_reponse_de_la_plateforme_mot_pour_mot()
    {
        // « 400 Bad Request » seul ne dit pas ce qui cloche : c'est le corps
        // qui le dit, et il doit se lire depuis l'écran distant.
        var demandes = new DemandesFeintes(Demande());
        var espion = new CertificateurEspion(new IssueCertification(
            false, "Refusée : la plateforme a répondu 400.",
            SageFne.Core.Fne.EtatFne.Error,
            ReponsePlateforme: """{"message":"Establishment is invalid"}"""));

        await new TraiteurDemandes(demandes, espion, NullLogger<TraiteurDemandes>.Instance)
            .TraiterAsync(10);

        var verdict = Assert.Single(demandes.Verdicts);
        Assert.False(verdict.Reussi);
        Assert.Contains("Establishment is invalid", verdict.Resultat);
    }

    [Fact]
    public async Task Le_plafond_d_envois_vaut_aussi_pour_les_demandes()
    {
        var demandes = new DemandesFeintes(
            Demande("d1", piece: "1"), Demande("d2", piece: "2"), Demande("d3", piece: "3"));

        var traiteur = new TraiteurDemandes(
            demandes, Certificateur(), NullLogger<TraiteurDemandes>.Instance);

        await traiteur.TraiterAsync(2);

        // Trois demandes en base, deux lues : le plafond borne ce qui part vers
        // la DGI, quelle qu'en soit l'origine.
        Assert.Equal(2, demandes.Prises.Count);
    }

    private static CertificateurEspion Certificateur(bool reussi = true) =>
        new(new IssueCertification(reussi, reussi ? "Certifiée." : "Refusée.",
            SageFne.Core.Fne.EtatFne.Certified, "2304903U26000000020"));

    // --- Le client HTTP -----------------------------------------------------

    private sealed class Repondeur(HttpStatusCode code, string corps) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Recues { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage requete, CancellationToken ct)
        {
            Recues.Add(requete);
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(corps),
            });
        }
    }

    private static OptionsSaas Reglages() => new()
    {
        Url = "https://abcdefgh.supabase.co",
        CleService = "service-role-secret",
        DossierId = "22222222-2222-2222-2222-222222222222",
    };

    private static DemandesHttp Client(Repondeur repondeur) =>
        new(new HttpClient(repondeur), Reglages(), NullLogger<DemandesHttp>.Instance);

    [Fact]
    public async Task La_lecture_ne_demande_que_les_demandes_du_dossier_en_attente()
    {
        var repondeur = new Repondeur(HttpStatusCode.OK, "[]");

        await Client(repondeur).EnAttenteAsync(10);

        var url = Uri.UnescapeDataString(repondeur.Recues[0].RequestUri!.ToString());
        Assert.Contains("dossier_id=eq.22222222-2222-2222-2222-222222222222", url);
        Assert.Contains("etat=eq.en_attente", url);
    }

    [Fact]
    public async Task La_reservation_est_conditionnee_sur_l_etat_en_attente()
    {
        // Sans cette condition, deux agents prendraient la même demande et la
        // facture partirait deux fois. C'est la base qui tranche.
        var repondeur = new Repondeur(HttpStatusCode.OK, """[{"id":"d1"}]""");

        Assert.True(await Client(repondeur).PrendreAsync("d1"));
        Assert.Contains("etat=eq.en_attente",
            Uri.UnescapeDataString(repondeur.Recues[0].RequestUri!.ToString()));
    }

    [Fact]
    public async Task Une_reservation_qui_ne_touche_aucune_ligne_est_un_refus()
    {
        var repondeur = new Repondeur(HttpStatusCode.OK, "[]");

        Assert.False(await Client(repondeur).PrendreAsync("d1"));
    }

    [Fact]
    public async Task La_cle_de_service_voyage_en_entete_et_pas_dans_l_URL()
    {
        var repondeur = new Repondeur(HttpStatusCode.OK, "[]");

        await Client(repondeur).EnAttenteAsync(10);

        Assert.True(repondeur.Recues[0].Headers.Contains("apikey"));
        Assert.DoesNotContain("service-role-secret", repondeur.Recues[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Une_base_injoignable_ne_leve_pas_et_ne_perd_rien()
    {
        // Les demandes restent en base et seront relues au tour suivant. Ce
        // chemin ne doit jamais empêcher l'agent de faire son travail.
        var repondeur = new Repondeur(HttpStatusCode.InternalServerError, "");

        Assert.Empty(await Client(repondeur).EnAttenteAsync(10));
    }

    // --- L'écran distant ----------------------------------------------------

    private static string Page(string nom)
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "SageFne.sln")))
        {
            dossier = dossier.Parent;
        }

        Assert.NotNull(dossier);
        return File.ReadAllText(Path.Combine(dossier!.FullName, "web", nom));
    }

    [Fact]
    public void L_ecran_distant_ne_porte_aucune_cle_de_service()
    {
        // La clé anon est faite pour vivre dans un navigateur ; la clé de
        // service donne un accès complet à la base et vit sur le poste.
        var page = Page("index.html") + Page("config.js");

        Assert.DoesNotContain("service_role", page);
        Assert.DoesNotContain("CleService", page);
        Assert.DoesNotContain("cleService", page);
    }

    [Fact]
    public void L_ecran_distant_refuse_de_tourner_sur_un_gabarit()
    {
        // Cinq incidents de ce projet viennent d'un exemple pris pour une
        // valeur, dont un où la DGI a refusé toutes les factures.
        var page = Page("index.html");

        Assert.Contains("A_COMPLETER", page);
        Assert.Contains("GABARIT", page);
        Assert.Contains("A_COMPLETER", Page("config.js"));
    }

    [Fact]
    public void L_ecran_distant_ne_propose_le_bouton_que_sur_ce_qui_peut_repartir()
    {
        // Une pièce certifiée, en cours d'envoi ou déposée au portail ne
        // repart pas : la règle vit dans le registre, et l'écran ne fait que
        // s'y conformer. L'agent refuserait de toute façon.
        var page = Page("index.html");

        var debut = page.IndexOf("const DEMANDABLE", StringComparison.Ordinal);
        Assert.True(debut > 0, "la liste des états demandables a disparu");
        var ligne = page[debut..page.IndexOf(';', debut)];

        Assert.DoesNotContain("certified", ligne);
        Assert.DoesNotContain("sending", ligne);
        Assert.DoesNotContain("transmise", ligne);
    }

    [Fact]
    public void L_ecran_distant_envoie_des_codes_et_non_des_libelles()
    {
        // Le portail affiche « Virement », l'API attend « transfer » : envoyer
        // le libellé ferait refuser la facture.
        var page = Page("index.html");

        foreach (var code in new[] { "cash", "card", "check", "mobile-money", "transfer", "deferred" })
        {
            Assert.Contains($"'{code}'", page);
        }
    }
}
