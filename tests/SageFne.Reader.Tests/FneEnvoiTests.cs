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
/// Le format de la réponse de la DGI n'est pas connu d'avance. Échouer sur un
/// nom de champ après qu'une facture a été certifiée serait le pire des cas :
/// la plateforme l'aurait enregistrée, et nous l'ignorerions.
/// </summary>
public class FneReponseTests
{
    [Theory]
    [InlineData("""{"reference":"2304903U26000000930"}""")]
    [InlineData("""{"referenceFne":"2304903U26000000930"}""")]
    [InlineData("""{"invoiceReference":"2304903U26000000930"}""")]
    [InlineData("""{"data":{"reference":"2304903U26000000930"}}""")]
    public void La_reference_se_lit_sous_plusieurs_noms(string corps)
    {
        var (reference, _) = FneApiClient.LireReponse(corps);

        Assert.Equal("2304903U26000000930", reference);
    }

    [Fact]
    public void Le_jeton_de_verification_se_lit_aussi()
    {
        var (_, jeton) = FneApiClient.LireReponse("""{"reference":"REF","token":"QR-123"}""");

        Assert.Equal("QR-123", jeton);
    }

    [Fact]
    public void Une_reference_numerique_est_acceptee()
    {
        var (reference, _) = FneApiClient.LireReponse("""{"reference":123456}""");

        Assert.Equal("123456", reference);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pas du json")]
    [InlineData("""{"autre":"chose"}""")]
    [InlineData("[1, 2, 3]")]
    public void Une_reponse_illisible_ne_leve_pas(string corps)
    {
        var (reference, jeton) = FneApiClient.LireReponse(corps);

        Assert.Null(reference);
        Assert.Null(jeton);
    }
}

/// <summary>La clé ne doit jamais s'afficher en clair.</summary>
public class FneApiOptionsTests
{
    [Fact]
    public void La_cle_est_masquee_a_l_affichage()
    {
        var options = new FneApiOptions { ApiKey = "sk-1234567890abcdef" };

        var masquee = options.CleMasquee();

        Assert.DoesNotContain("567890ab", masquee);
        Assert.StartsWith("sk-1", masquee);
        Assert.EndsWith("cdef", masquee);
    }

    [Fact]
    public void Une_cle_courte_disparait_entierement()
    {
        Assert.DoesNotContain("abcd", new FneApiOptions { ApiKey = "abcd" }.CleMasquee());
    }

    [Fact]
    public void Sans_url_ni_cle_rien_ne_peut_partir()
    {
        Assert.False(new FneApiOptions().EstConfigure);
        Assert.False(new FneApiOptions { BaseUrl = "http://54.247.95.108/ws" }.EstConfigure);
        Assert.False(new FneApiOptions { BaseUrl = "A_COMPLETER", ApiKey = "k" }.EstConfigure);
        Assert.True(new FneApiOptions { BaseUrl = "http://54.247.95.108/ws", ApiKey = "k" }.EstConfigure);
    }

    [Fact]
    public void L_adresse_se_compose_sans_double_barre()
    {
        var options = new FneApiOptions
        {
            BaseUrl = "http://54.247.95.108/ws/",
            SignPath = "/external/invoices/sign",
        };

        Assert.Equal(
            "http://54.247.95.108/ws/external/invoices/sign",
            options.AdresseSignature().ToString());
    }
}

/// <summary>
/// L'ordre des opérations est la seule protection contre le doublon : une
/// facture partie dont la réponse se perd doit laisser une trace.
/// </summary>
public class InvoiceSenderTests
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

    private sealed class ClientFactice(FneSignResult reponse) : IFneApiClient
    {
        public bool Appele { get; private set; }

        /// <summary>Ce que le registre contenait au moment de l'appel.</summary>
        public List<CertifiedInvoice> RegistreAuMomentDeLAppel { get; } = [];

        public RegistreEspion? Registre { get; set; }

        public bool Reel => false;

        public string DecrireRequete(FneInvoice facture) => "POST …";

        public Task<FneSignResult> SignAsync(FneInvoice facture, CancellationToken ct = default)
        {
            Appele = true;
            if (Registre is not null) RegistreAuMomentDeLAppel.AddRange(Registre.Ecritures);
            return Task.FromResult(reponse);
        }
    }

    private static (InvoiceSender Expediteur, RegistreEspion Registre, ClientFactice Client) Monter(
        FneSignResult reponse)
    {
        var registre = new RegistreEspion();
        var client = new ClientFactice(reponse) { Registre = registre };
        var options = Options.Create(new FneOptions
        {
            PointOfSale = "SIEGE",
            Establishment = "PRINCIPAL",
            Template = "B2B",
            PaymentMethod = "deferred",
        });

        var lecteur = new InvoiceBatchReader(
            new DemoSageInvoiceRepository(), new FneInvoiceMapper(options), registre, options);

        return (new InvoiceSender(lecteur, registre, client, NullLogger<InvoiceSender>.Instance),
            registre, client);
    }

    /// <summary>1221 : TVA 9 %, NCC présent, aucun blocage dans le jeu d'essai.</summary>
    private const string Envoyable = "1221";

    /// <summary>1222 : client sans NCC, donc bloquée.</summary>
    private const string Bloquee = "1222";

    [Fact]
    public async Task Sans_confirmation_rien_ne_part_et_rien_n_est_inscrit()
    {
        var (expediteur, registre, client) = Monter(new FneSignResult(true, 200, "REF"));

        var resultat = await expediteur.EnvoyerAsync(Envoyable, confirme: false);

        Assert.Equal(EtatFne.Ready, resultat.Etat);
        Assert.False(client.Appele);
        Assert.Empty(registre.Ecritures);
    }

    [Fact]
    public async Task Une_piece_bloquee_ne_part_pas()
    {
        var (expediteur, registre, client) = Monter(new FneSignResult(true, 200, "REF"));

        var resultat = await expediteur.EnvoyerAsync(Bloquee, confirme: true);

        Assert.Equal(EtatFne.Error, resultat.Etat);
        Assert.False(client.Appele);
        Assert.Empty(registre.Ecritures);
    }

    [Fact]
    public async Task Une_piece_inconnue_ne_part_pas()
    {
        var (expediteur, _, client) = Monter(new FneSignResult(true, 200, "REF"));

        var resultat = await expediteur.EnvoyerAsync("999999", confirme: true);

        Assert.Equal(EtatFne.Error, resultat.Etat);
        Assert.False(client.Appele);
    }

    [Fact]
    public async Task Sending_est_inscrit_AVANT_l_appel()
    {
        // La propriété qui protège du doublon : si la machine s'arrête entre
        // l'appel et la réponse, la trace existe déjà.
        var (expediteur, _, client) = Monter(new FneSignResult(true, 200, "REF"));

        await expediteur.EnvoyerAsync(Envoyable, confirme: true);

        var avantAppel = Assert.Single(client.RegistreAuMomentDeLAppel);
        Assert.Equal(EtatFne.Sending, avantAppel.Etat);
        Assert.Equal(Envoyable, avantAppel.Piece);
    }

    [Fact]
    public async Task Un_succes_inscrit_la_reference_et_l_etat_Certified()
    {
        var (expediteur, registre, _) = Monter(
            new FneSignResult(true, 200, "2304903U26000000930", "QR", """{"reference":"…"}"""));

        var resultat = await expediteur.EnvoyerAsync(Envoyable, confirme: true);

        Assert.Equal(EtatFne.Certified, resultat.Etat);
        Assert.True(resultat.Reussi);

        var derniere = registre.Ecritures[^1];
        Assert.Equal(EtatFne.Certified, derniere.Etat);
        Assert.Equal("2304903U26000000930", derniere.ReferenceFne);
        Assert.Equal("QR", derniere.Token);
        Assert.NotEqual("", derniere.Empreinte);
    }

    [Fact]
    public async Task Un_refus_franc_devient_une_erreur()
    {
        var (expediteur, registre, _) = Monter(
            new FneSignResult(false, 422, CorpsBrut: "{}", Erreur: "NCC invalide"));

        var resultat = await expediteur.EnvoyerAsync(Envoyable, confirme: true);

        Assert.Equal(EtatFne.Error, resultat.Etat);
        Assert.Equal(EtatFne.Error, registre.Ecritures[^1].Etat);
    }

    [Fact]
    public async Task Un_delai_depasse_laisse_la_piece_en_Sending()
    {
        // Sans code HTTP, on ignore ce que la DGI a enregistré. Repasser en
        // Error autoriserait un renvoi, donc un doublon possible.
        var (expediteur, registre, _) = Monter(
            new FneSignResult(false, Erreur: "délai de 30 s dépassé sans réponse."));

        var resultat = await expediteur.EnvoyerAsync(Envoyable, confirme: true);

        Assert.Equal(EtatFne.Sending, resultat.Etat);
        Assert.Equal(EtatFne.Sending, registre.Ecritures[^1].Etat);
        Assert.Contains("Issue inconnue", resultat.Message);
    }

    [Fact]
    public async Task Une_reponse_acceptee_sans_reference_reste_en_Sending()
    {
        // 200 mais rien de lisible : la facture est peut-être certifiée.
        var (expediteur, registre, _) = Monter(
            new FneSignResult(false, 200, CorpsBrut: """{"ok":true}""", Erreur: "aucune référence lisible"));

        var resultat = await expediteur.EnvoyerAsync(Envoyable, confirme: true);

        Assert.Equal(EtatFne.Sending, resultat.Etat);
        Assert.Equal(EtatFne.Sending, registre.Ecritures[^1].Etat);
    }

    [Fact]
    public async Task La_reponse_brute_est_toujours_conservee()
    {
        var (expediteur, registre, _) = Monter(
            new FneSignResult(false, 500, CorpsBrut: "erreur interne", Erreur: "500"));

        await expediteur.EnvoyerAsync(Envoyable, confirme: true);

        Assert.Equal("erreur interne", registre.Ecritures[^1].Reponse);
    }

    [Fact]
    public async Task L_identite_inscrite_survit_a_la_comptabilisation()
    {
        var (expediteur, registre, _) = Monter(new FneSignResult(true, 200, "REF"));

        await expediteur.EnvoyerAsync(Envoyable, confirme: true);

        // domaine / type d'origine / pièce : la clé ne bouge pas quand DO_Type
        // passe de 6 à 7.
        Assert.Equal($"0/6/{Envoyable}", registre.Ecritures[^1].Identite);
    }
}

/// <summary>
/// Un registre qui ne peut pas écrire doit arrêter l'envoi. Une facture
/// certifiée par la DGI dont nous n'aurions aucune trace serait pire que pas de
/// facture du tout : elle repartirait en double au prochain lot.
/// </summary>
public class RegistreIndisponibleTests
{
    private sealed class RegistreEnPanne : ICertificationLedger
    {
        public Task<IReadOnlyDictionary<string, CertifiedInvoice>> LookupAsync(
            IReadOnlyCollection<string> identites, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, CertifiedInvoice>>(
                new Dictionary<string, CertifiedInvoice>());

        public Task RecordAsync(CertifiedInvoice certification, CancellationToken ct = default) =>
            throw new IOException("disque plein");
    }

    private sealed class ClientTemoin : IFneApiClient
    {
        public bool Appele { get; private set; }
        public bool Reel => false;
        public string DecrireRequete(SageFne.Reader.Models.Fne.FneInvoice facture) => "";

        public Task<FneSignResult> SignAsync(
            SageFne.Reader.Models.Fne.FneInvoice facture, CancellationToken ct = default)
        {
            Appele = true;
            return Task.FromResult(new FneSignResult(true, 200, "REF"));
        }
    }

    [Fact]
    public async Task Un_registre_en_panne_arrete_l_envoi_avant_tout_appel()
    {
        var client = new ClientTemoin();
        var options = Options.Create(new FneOptions
        {
            PointOfSale = "FISH-AFRIC",
            Establishment = "FISH-AFRIC",
            Template = "B2B",
            PaymentMethod = "deferred",
        });
        var registre = new RegistreEnPanne();
        var lecteur = new InvoiceBatchReader(
            new DemoSageInvoiceRepository(), new FneInvoiceMapper(options), registre, options);
        var expediteur = new InvoiceSender(
            lecteur, registre, client, NullLogger<InvoiceSender>.Instance);

        var resultat = await expediteur.EnvoyerAsync("1221", confirme: true);

        Assert.False(client.Appele, "aucun appel ne doit partir si la trace est impossible");
        Assert.Equal(EtatFne.Error, resultat.Etat);
        Assert.Contains("disque plein", resultat.Message);
        Assert.Contains("Rien n'a été envoyé", resultat.Message);
    }
}

/// <summary>
/// Le chemin du registre : le vide compte comme absent.
/// </summary>
public class CheminRegistreTests
{
    private const string Dossier = "/app";

    [Fact]
    public void Un_chemin_vide_en_configuration_retombe_sur_le_defaut()
    {
        // appsettings.json porte « "CertificationLedgerPath": "" » : un simple
        // ?? ne retombait pas sur le défaut, et le registre recevait "".
        var chemin = ServicesMiddleware.CheminRegistre(null, "", Dossier, connexionSageConfiguree: true);

        Assert.Equal(Path.Combine(Dossier, "certifications.json"), chemin);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Sans_connexion_Sage_le_registre_reste_en_memoire(string? configure)
    {
        Assert.Null(ServicesMiddleware.CheminRegistre(null, configure, Dossier, false));
    }

    [Fact]
    public void La_ligne_de_commande_l_emporte_sur_la_configuration()
    {
        Assert.Equal(
            "/tmp/mien.json",
            ServicesMiddleware.CheminRegistre("/tmp/mien.json", "/autre.json", Dossier, true));
    }

    [Fact]
    public void Un_chemin_configure_est_respecte_meme_sans_connexion()
    {
        // Demander un registre, c'est vouloir qu'il existe.
        Assert.Equal(
            "/data/reg.json",
            ServicesMiddleware.CheminRegistre(null, "/data/reg.json", Dossier, false));
    }

    [Fact]
    public void Les_espaces_autour_sont_retires()
    {
        Assert.Equal("/data/reg.json",
            ServicesMiddleware.CheminRegistre("  /data/reg.json  ", null, Dossier, true));
    }

    [Fact]
    public void Un_registre_sans_chemin_refuse_d_exister()
    {
        // Mieux vaut échouer à la construction qu'au milieu d'un envoi.
        var erreur = Assert.Throws<ArgumentException>(
            () => new JsonCertificationLedger("", NullLogger<JsonCertificationLedger>.Instance));

        Assert.Contains("Fne:CertificationLedgerPath", erreur.Message);
    }
}
