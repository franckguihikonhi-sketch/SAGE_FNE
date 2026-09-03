using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SageFne.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SageFne.Core.Certification;
using SageFne.Core.Fne;
using SageFne.Core.Saas;

namespace SageFne.Agent.Tests;

/// <summary>
/// Le miroir vers la base d'audit : un reflet, jamais une autorité.
/// </summary>
/// <remarks>
/// Le registre fichier reste la seule mémoire des certifications. Ce que ces
/// tests tiennent, c'est que le miroir ne puisse jamais en devenir une seconde
/// — ni en bloquant un envoi, ni en modifiant un état, ni en faisant échouer un
/// tour parce qu'un serveur est injoignable.
///
/// La leçon est fraîche : une facture a été certifiée deux fois parce que deux
/// registres se croyaient tous deux vrais.
/// </remarks>
public class MiroirTests
{
    private static OptionsSaas Reglages(
        string url = "https://abcdefgh.supabase.co",
        string cle = "service-role-secret",
        string dossier = "22222222-2222-2222-2222-222222222222") =>
        new() { Url = url, CleService = cle, DossierId = dossier };

    private static CertifiedInvoice Trace(
        EtatFne etat = EtatFne.Certified,
        string reference = "2304903U26000000002",
        string reponse = "") => new()
    {
        Identite = "0/6/1225",
        Piece = "1225",
        Empreinte = "abc123",
        Etat = etat,
        ReferenceFne = reference,
        Reponse = reponse,
        CertifieeLe = new DateTimeOffset(2026, 9, 2, 13, 42, 0, TimeSpan.Zero),
    };

    private sealed class Repondeur(HttpStatusCode code, string corps = "")
        : HttpMessageHandler
    {
        public HttpRequestMessage? Recue { get; private set; }
        public string CorpsRecu { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage requete, CancellationToken ct)
        {
            Recue = requete;
            CorpsRecu = requete.Content is null ? "" : await requete.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(code) { Content = new StringContent(corps) };
        }
    }

    private sealed class Injoignable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage requete, CancellationToken ct) =>
            throw new HttpRequestException("nom d'hôte introuvable");
    }

    private static MiroirHttp Miroir(HttpMessageHandler handler, OptionsSaas? reglages = null) =>
        new(new HttpClient(handler), reglages ?? Reglages(), NullLogger<MiroirHttp>.Instance);

    private static LigneMiroir Ligne(CertifiedInvoice? trace = null) =>
        MiroirSaas.Traduire(trace ?? Trace(), "22222222-2222-2222-2222-222222222222", production: false);

    // --- Inerte tant qu'il n'est pas configuré ------------------------------

    [Theory]
    [InlineData("", "cle", "dossier")]
    [InlineData("https://x.supabase.co", "", "dossier")]
    [InlineData("https://x.supabase.co", "cle", "")]
    public void Une_configuration_incomplete_laisse_le_miroir_eteint(
        string url, string cle, string dossier) =>
        Assert.False(new OptionsSaas { Url = url, CleService = cle, DossierId = dossier }.Actif);

    [Theory]
    [InlineData("VOTRE_PROJET.supabase.co", "cle", "dossier")]
    [InlineData("A_COMPLETER", "cle", "dossier")]
    [InlineData("https://x.supabase.co", "cle", "A_COMPLETER")]
    public void Un_gabarit_non_remplace_ne_compte_pas_comme_une_configuration(
        string url, string cle, string dossier) =>
        Assert.False(Reglages(url, cle, dossier).Actif);

    [Theory]
    [InlineData("<service_role>")]
    [InlineData("[service_role]")]
    [InlineData("A_COMPLETER")]
    [InlineData("VOTRE_CLE")]
    public void Une_cle_restee_au_gabarit_laisse_le_miroir_eteint(string cle)
    {
        // Vécu : la ligne de documentation a été collée telle quelle dans la
        // variable machine, chevrons compris. La valeur n'était pas vide, le
        // miroir se croyait allumé, et chaque tour partait chercher un 401.
        // Sixième fois qu'un exemple de ce projet passe pour une valeur.
        Assert.False(Reglages(cle: cle).Actif);
    }

    [Fact]
    public void La_documentation_du_SaaS_ne_porte_aucun_gabarit_entre_chevrons()
    {
        // PowerShell refuse les chevrons — « l'opérateur < est réservé » — et
        // une valeur qui en porte s'installe pourtant en silence quand la
        // commande passe. Les emplacements s'écrivent A_COMPLETER, que le
        // middleware reconnaît et refuse.
        var readme = Page("README.md");

        Assert.DoesNotContain("<", readme);
        Assert.Contains("A_COMPLETER", readme);

        // Et « && » n'est pas un séparateur d'instruction sous PowerShell 5.1.
        foreach (var bloc in readme.Split("```powershell").Skip(1))
        {
            Assert.DoesNotContain("&&", bloc[..bloc.IndexOf("```", StringComparison.Ordinal)]);
        }
    }

    [Fact]
    public void Toute_invocation_du_script_passe_par_ExecutionPolicy_Bypass()
    {
        // Windows refuse par défaut d'exécuter un .ps1, et son message ne dit
        // pas quoi faire. Une commande de documentation qui l'oublie envoie
        // l'exploitant dans un mur.
        var readme = Page("README.md");

        foreach (var ligne in readme.Split('\n').Where(l => l.Contains("installer-agent.ps1")))
        {
            Assert.Contains("-ExecutionPolicy Bypass", ligne);
        }
    }

    [Fact]
    public async Task Eteint_le_miroir_ne_forme_aucune_requete()
    {
        var repondeur = new Repondeur(HttpStatusCode.Created);
        var miroir = Miroir(repondeur, Reglages(cle: ""));

        var resultat = await miroir.PublierAsync([Ligne()]);

        Assert.Null(repondeur.Recue);
        Assert.False(miroir.Actif);
        Assert.Equal(0, resultat.Publiees);
    }

    // --- Ce qui part, et ce qui ne part jamais ------------------------------

    [Fact]
    public async Task La_ligne_publiee_porte_les_colonnes_du_schema()
    {
        var repondeur = new Repondeur(HttpStatusCode.Created);

        await Miroir(repondeur).PublierAsync([Ligne()]);

        using var envoye = JsonDocument.Parse(repondeur.CorpsRecu);
        var ligne = envoye.RootElement[0];

        Assert.Equal("0/6/1225", ligne.GetProperty("identite").GetString());
        Assert.Equal("certified", ligne.GetProperty("etat").GetString());
        Assert.Equal("test", ligne.GetProperty("environnement").GetString());
        Assert.Equal("2304903U26000000002", ligne.GetProperty("reference_fne").GetString());
    }

    [Fact]
    public async Task Aucune_ligne_de_facture_ne_part_vers_le_cloud()
    {
        // La base n'en veut pas, et son README le dit : de quoi retrouver une
        // facture, pas de quoi la reconstituer.
        var repondeur = new Repondeur(HttpStatusCode.Created);

        await Miroir(repondeur).PublierAsync([Ligne()]);

        foreach (var interdit in new[] { "items", "designation", "prixUnitaire", "clientEmail" })
        {
            Assert.DoesNotContain(interdit, repondeur.CorpsRecu, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task La_cle_de_service_ne_voyage_qu_en_entete()
    {
        var repondeur = new Repondeur(HttpStatusCode.Created);

        await Miroir(repondeur).PublierAsync([Ligne()]);

        Assert.DoesNotContain("service-role-secret", repondeur.CorpsRecu);
        Assert.True(repondeur.Recue!.Headers.Contains("apikey"));
    }

    [Fact]
    public void La_cle_ne_s_affiche_jamais_en_clair()
    {
        var masquee = Reglages().CleMasquee();

        Assert.DoesNotContain("service-role-secret", masquee);
        Assert.Contains("•", masquee);
    }

    [Fact]
    public async Task La_republication_met_a_jour_au_lieu_de_multiplier()
    {
        // Sans cela, un tour par minute créerait mille lignes par jour pour une
        // seule facture — et la contrainte d'unicité refuserait tout.
        var repondeur = new Repondeur(HttpStatusCode.Created);

        await Miroir(repondeur).PublierAsync([Ligne()]);

        Assert.Contains("on_conflict=dossier_id,environnement,identite",
            Uri.UnescapeDataString(repondeur.Recue!.RequestUri!.Query));
        Assert.Contains("merge-duplicates", string.Join(",", repondeur.Recue.Headers.GetValues("Prefer")));
    }

    // --- Ce qu'un échec doit faire, et surtout ne pas faire -----------------

    [Fact]
    public async Task Une_base_injoignable_ne_leve_pas()
    {
        var resultat = await Miroir(new Injoignable()).PublierAsync([Ligne()]);

        Assert.False(resultat.Aboutie);
        Assert.Equal(0, resultat.Refusees);
        Assert.Contains("injoignable", resultat.Empechement!);
    }

    [Fact]
    public async Task Un_refus_de_la_base_se_distingue_d_une_panne()
    {
        // Le schéma dit non à ce que le registre affirme : ce n'est pas un
        // incident de transport, c'est un désaccord de fond.
        var resultat = await Miroir(new Repondeur(
            HttpStatusCode.BadRequest,
            """{"message":"transition interdite : certified -> error"}"""))
            .PublierAsync([Ligne()]);

        Assert.Equal(1, resultat.Refusees);
        Assert.Contains("transition interdite", resultat.Detail);
    }

    [Fact]
    public async Task Une_panne_serveur_n_est_pas_comptee_comme_un_refus()
    {
        var resultat = await Miroir(new Repondeur(HttpStatusCode.BadGateway)).PublierAsync([Ligne()]);

        Assert.Equal(0, resultat.Refusees);
        Assert.False(resultat.Aboutie);
    }

    // --- La traduction ------------------------------------------------------

    [Fact]
    public void Une_reponse_illisible_ne_fait_pas_perdre_la_ligne()
    {
        // La colonne est jsonb : une page HTML d'erreur y ferait refuser toute
        // la ligne, et l'état vaut plus que le corps de la réponse.
        var ligne = Ligne(Trace(reponse: "<html>502 Bad Gateway</html>"));

        Assert.Null(ligne.Reponse);
        Assert.Equal("certified", ligne.Etat);
    }

    [Fact]
    public void Une_reference_vide_part_en_nul_et_non_en_chaine_vide()
    {
        // La contrainte SQL veut une référence sur une pièce certifiée, et
        // refuse la chaîne vide comme le nul. Mais sur une pièce en erreur,
        // « » et « rien » ne disent pas la même chose.
        Assert.Null(Ligne(Trace(EtatFne.Error, reference: "")).ReferenceFne);
    }

    [Theory]
    [InlineData(EtatFne.Certified, "certified")]
    [InlineData(EtatFne.Sending, "sending")]
    [InlineData(EtatFne.Transmise, "transmise")]
    [InlineData(EtatFne.Error, "error")]
    public void Les_etats_portent_le_meme_nom_des_deux_cotes(EtatFne etat, string attendu) =>
        Assert.Equal(attendu, Ligne(Trace(etat, reference: "REF")).Etat);

    // --- Le câblage, et la frontière ----------------------------------------

    private static ServiceProvider Cabler(params (string Cle, string Valeur)[] reglages)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(reglages.Select(r =>
                new KeyValuePair<string, string?>(r.Cle, r.Valeur)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AjouterMiddlewareFne(configuration, chaineSage: "", cheminRegistre: null);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Sans_section_Saas_le_miroir_est_resolvable_et_eteint()
    {
        // Un poste qui certifie aujourd'hui doit continuer exactement comme
        // avant, sans qu'aucun réglage nouveau soit posé.
        using var fournisseur = Cabler();

        Assert.False(fournisseur.GetRequiredService<IMiroirClient>().Actif);
    }

    [Fact]
    public void La_section_Saas_allume_le_miroir()
    {
        using var fournisseur = Cabler(
            ("Saas:Url", "https://abcdefgh.supabase.co"),
            ("Saas:CleService", "secret"),
            ("Saas:DossierId", "22222222-2222-2222-2222-222222222222"));

        Assert.True(fournisseur.GetRequiredService<IMiroirClient>().Actif);
    }

    private static IEnumerable<Type> Signatures(Type type) => type
        .GetConstructors()
        .SelectMany(c => c.GetParameters().Select(p => p.ParameterType))
        .Concat(type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(f => f.FieldType))
        .Concat(type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)));

    [Fact]
    public void Rien_dans_le_chemin_d_envoi_ne_connait_le_miroir()
    {
        // La frontière qui compte. Le miroir REFLÈTE après coup : s'il pouvait
        // être consulté avant un envoi, une base injoignable finirait par
        // empêcher une certification que le poste peut parfaitement faire seul.
        //
        // À distinguer de la réservation, qui DÉCIDE et doit donc y entrer :
        // le test suivant l'exige.
        var interdits = new[]
        {
            typeof(SageFne.Core.Fne.InvoiceSender),
            typeof(SageFne.Agent.Surveillance.MoteurSurveillance),
            typeof(SageFne.Core.Batch.InvoiceBatchReader),
        };

        foreach (var type in interdits)
        {
            Assert.DoesNotContain(Signatures(type), t => t == typeof(IMiroirClient));
        }
    }

    [Fact]
    public void L_expediteur_consulte_la_reservation_partagee()
    {
        // L'inverse du test précédent, et c'est voulu. Le registre fichier ne
        // connaît que son poste : deux postes, deux registres qui s'ignorent,
        // et la même pièce part deux fois. C'est arrivé sur la 1225, à sept
        // heures d'intervalle, sans que personne édite quoi que ce soit dans
        // Sage — le verrou de saisie de Sage ne protège pas d'une lecture.
        Assert.Contains(
            Signatures(typeof(SageFne.Core.Fne.InvoiceSender)),
            t => t == typeof(IReservationClient));
    }

    [Fact]
    public void La_reservation_reste_hors_du_moteur_de_surveillance()
    {
        // Elle appartient au seul endroit qui envoie. L'examen des pièces, lui,
        // doit continuer même si la base d'audit est muette : un poste isolé
        // travaille comme avant.
        Assert.DoesNotContain(
            Signatures(typeof(SageFne.Agent.Surveillance.MoteurSurveillance)),
            t => t == typeof(IReservationClient));
    }

    // --- L'installeur -------------------------------------------------------

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

    private static string Installeur()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "SageFne.sln")))
        {
            dossier = dossier.Parent;
        }

        Assert.NotNull(dossier);
        return File.ReadAllText(Path.Combine(dossier!.FullName, "deploiement", "installer-agent.ps1"));
    }

    [Fact]
    public void La_section_Saas_est_reprise_a_chaque_republication()
    {
        // Un reglage qu'on cesse de porter est un reglage perdu : FenetreJours
        // est deja retombe de 30 a 7 de cette facon, et l'identite du dossier a
        // « A_COMPLETER ». La section Saas ne doit pas suivre le meme chemin.
        var script = Installeur();

        Assert.Contains("$saasEnPlace = $ancien.Saas", script);
        Assert.Contains("Fixer-Propriete $config.Saas $champ.Name $valeur", script);
    }

    [Fact]
    public void La_cle_de_service_ne_va_jamais_dans_appsettings()
    {
        // Elle donne un acces complet a la base. Variable machine, comme la
        // cle FNE, et rien d'autre.
        var script = Installeur();

        Assert.Contains("Saas__CleService", script);
        Assert.DoesNotContain("Fixer-Propriete $config.Saas 'CleService'", script);
        Assert.DoesNotContain("$config.Saas.CleService", script);
    }

    [Fact]
    public void L_etat_du_miroir_est_affiche_meme_eteint()
    {
        // Une fonction dont on ignore qu'elle est eteinte se croit en panne :
        // c'est deja arrive deux fois sur ce produit.
        Assert.Contains("Base d'audit      eteinte", Installeur());
    }
}
