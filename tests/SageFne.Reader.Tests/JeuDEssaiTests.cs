using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SageFne.Core.Batch;
using SageFne.Core.Certification;
using SageFne.Core.Configuration;
using SageFne.Core.Data;
using SageFne.Core.Fne;
using SageFne.Core.Mapping;
using SageFne.Core.Models.Fne;

namespace SageFne.Core.Tests;

/// <summary>
/// Une facture inventée ne s'envoie pas à la DGI.
/// </summary>
/// <remarks>
/// Le refus existait — dans la commande <c>envoyer</c> du CLI, c'est-à-dire chez
/// l'appelant. L'agent, deuxième appelant, ne l'a jamais eu ; le tableau de bord,
/// troisième, non plus.
///
/// Le défaut a été constaté en cliquant sur « Certifier » dans le tableau, sur le
/// jeu d'essai : un POST est réellement parti vers la plateforme de la DGI avec
/// une facture fabriquée. Seule une clé d'API invalide l'a arrêté — un
/// <c>401</c>. Avec une clé valide, la pièce « DEMO SA (jeu d'essai) » aurait été
/// certifiée, et n'aurait pu être défaite que par un avoir.
///
/// Ce n'est pas un cas de laboratoire : la lecture retombe sur le jeu d'essai dès
/// que <c>ConnectionStrings__Sage</c> n'atteint pas le service, ce qui est arrivé
/// sur le premier poste réel — le gestionnaire de services ne voit pas les
/// variables machine posées après l'amorçage de Windows.
///
/// La règle vit désormais dans <see cref="InvoiceSender"/>, seul chemin par
/// lequel tous les appelants passent.
/// </remarks>
public class JeuDEssaiTests
{
    /// <summary>Compte les appels au lieu d'en passer un.</summary>
    private sealed class ClientCompteur : IFneApiClient
    {
        public int Appels { get; private set; }
        public bool Reel => false;
        public string DecrireRequete(FneInvoice facture) => "";

        public Task<FneSignResult> SignAsync(FneInvoice facture, CancellationToken ct = default)
        {
            Appels++;
            return Task.FromResult(new FneSignResult(true, 201, "REFERENCE-INVENTEE"));
        }
    }

    private sealed class RegistreCompteur : ICertificationLedger
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

    private static (InvoiceSender Expediteur, ClientCompteur Client, RegistreCompteur Registre)
        Monter(bool estReel)
    {
        var reglages = ReglagesDEssai.SansDelaiPortail;
        var registre = new RegistreCompteur();
        var client = new ClientCompteur();
        var lecteur = new InvoiceBatchReader(
            new DemoSageInvoiceRepository(estReel),
            new FneInvoiceMapper(reglages),
            registre,
            reglages);

        return (new InvoiceSender(
            lecteur, registre, client, NullLogger<InvoiceSender>.Instance, reglages),
            client, registre);
    }

    [Fact]
    public async Task Une_facture_du_jeu_d_essai_ne_part_jamais()
    {
        var (expediteur, client, registre) = Monter(estReel: false);

        var resultat = await expediteur.EnvoyerAsync("1220", confirme: true);

        Assert.False(resultat.Reussi);
        Assert.Equal(0, client.Appels);

        // Rien au registre non plus : une pièce marquée « Sending » resterait
        // bloquée pour toujours, sans qu'aucun envoi n'ait eu lieu.
        Assert.Empty(registre.Ecritures);
        Assert.Contains("JEU D'ESSAI", resultat.Message);
    }

    [Fact]
    public async Task Le_refus_precede_meme_la_lecture_du_registre()
    {
        // L'ordre compte. L'expéditeur inscrit « Sending » AVANT l'appel, pour
        // qu'un doublon soit impossible si la réponse se perd. Un refus qui
        // arriverait après cette inscription laisserait la pièce en suspens
        // définitif, sur un envoi qui n'a jamais eu lieu.
        var (expediteur, _, registre) = Monter(estReel: false);

        await expediteur.EnvoyerAsync("1220", confirme: true);

        Assert.DoesNotContain(registre.Ecritures, e => e.Etat == EtatFne.Sending);
    }

    [Fact]
    public async Task Sur_un_vrai_dossier_l_envoi_se_fait()
    {
        // Le pendant du précédent : sans lui, un refus qui bloquerait tout
        // passerait pour une protection.
        var (expediteur, client, _) = Monter(estReel: true);

        var resultat = await expediteur.EnvoyerAsync("1220", confirme: true);

        Assert.Equal(1, client.Appels);
        Assert.True(resultat.Reussi);
    }

    /// <summary>Le paramétrage livré, dont l'identité reste à remplir.</summary>
    private static IOptions<FneOptions> Gabarit(string pointOfSale, string establishment) =>
        Options.Create(new FneOptions
        {
            PointOfSale = pointOfSale,
            Establishment = establishment,
            Template = "B2B",
            PaymentMethod = "deferred",
            PortalCheckDelayMinutes = 0,
        });

    [Theory]
    [InlineData("A_COMPLETER", "A_COMPLETER")]
    [InlineData("FISH-AFRIC", "A_COMPLETER")]
    [InlineData("A_COMPLETER", "FISH-AFRIC")]
    [InlineData("", "")]
    public async Task Une_identite_non_renseignee_arrete_l_envoi(string pos, string etab)
    {
        // La DGI a répondu « Establishment is invalid » sur quatre pièces
        // d'affilée, et rien ne pouvait le prévoir : le point de vente et
        // l'établissement viennent du paramétrage, pas de Sage, donc aucun
        // contrôle de pièce ne les regarde. Une facture irréprochable partait
        // avec « A_COMPLETER ».
        //
        // FneCompleteness voyait le cas, mais n'était appelé que par la commande
        // « apercu » du CLI — chez l'appelant, une fois de plus.
        var reglages = Gabarit(pos, etab);
        var registre = new RegistreCompteur();
        var client = new ClientCompteur();
        var lecteur = new InvoiceBatchReader(
            new DemoSageInvoiceRepository(estReel: true),
            new FneInvoiceMapper(reglages), registre, reglages);

        var resultat = await new InvoiceSender(
                lecteur, registre, client, NullLogger<InvoiceSender>.Instance, reglages)
            .EnvoyerAsync("1220", confirme: true);

        Assert.False(resultat.Reussi);
        Assert.Equal(0, client.Appels);

        // Et surtout : rien en Sending. Un envoi impossible ne doit pas laisser
        // une pièce en suspens, qui ne repartirait plus jamais toute seule.
        Assert.Empty(registre.Ecritures);
    }

    [Fact]
    public async Task Une_identite_renseignee_laisse_partir()
    {
        var reglages = Gabarit("FISH-AFRIC", "FISH-AFRIC");
        var registre = new RegistreCompteur();
        var client = new ClientCompteur();
        var lecteur = new InvoiceBatchReader(
            new DemoSageInvoiceRepository(estReel: true),
            new FneInvoiceMapper(reglages), registre, reglages);

        var resultat = await new InvoiceSender(
                lecteur, registre, client, NullLogger<InvoiceSender>.Instance, reglages)
            .EnvoyerAsync("1220", confirme: true);

        Assert.Equal(1, client.Appels);
        Assert.True(resultat.Reussi);
    }

    [Fact]
    public void Le_cablage_de_production_ne_declare_jamais_le_jeu_d_essai_reel()
    {
        // La seule chose qui sépare l'agent de la certification de factures
        // fabriquées, c'est ce booléen. Un « true » posé ici par inadvertance ne
        // se verrait nulle part ailleurs.
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AjouterMiddlewareFne(configuration, chaineSage: "", cheminRegistre: null);

        using var fournisseur = services.BuildServiceProvider();
        var depot = fournisseur.GetRequiredService<ISageInvoiceRepository>();

        Assert.IsType<DemoSageInvoiceRepository>(depot);
        Assert.False(depot.EstReel);
    }
}
