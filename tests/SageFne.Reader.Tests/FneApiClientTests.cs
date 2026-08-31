using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SageFne.Reader.Configuration;
using SageFne.Reader.Fne;
using SageFne.Reader.Models.Fne;

namespace SageFne.Reader.Tests;

/// <summary>
/// Le client n'avait jamais fait un vrai appel : ni en-tête vérifié, ni corps
/// sérialisé, ni réponse lue. Ces tests le confrontent à un serveur réel —
/// local, mais qui parle vraiment HTTP.
/// </summary>
public sealed class FneApiClientTests : IDisposable
{
    private readonly HttpListener _serveur = new();
    private readonly string _adresse;

    /// <summary>Ce que le serveur a reçu, pour le vérifier après coup.</summary>
    private string _corpsRecu = "";
    private string _enteteRecu = "";
    private string _methodeRecue = "";
    private string _cheminRecu = "";

    public FneApiClientTests()
    {
        var port = 8100 + Random.Shared.Next(400);
        _adresse = $"http://localhost:{port}/ws";
        _serveur.Prefixes.Add($"http://localhost:{port}/");
        _serveur.Start();
    }

    public void Dispose()
    {
        if (_serveur.IsListening) _serveur.Stop();
        ((IDisposable)_serveur).Dispose();
    }

    private void Repondre(HttpStatusCode code, string corps)
    {
        _ = Task.Run(async () =>
        {
            var contexte = await _serveur.GetContextAsync();

            _methodeRecue = contexte.Request.HttpMethod;
            _cheminRecu = contexte.Request.Url?.AbsolutePath ?? "";
            _enteteRecu = contexte.Request.Headers["Authorization"] ?? "";
            using (var lecteur = new StreamReader(contexte.Request.InputStream))
            {
                _corpsRecu = await lecteur.ReadToEndAsync();
            }

            var octets = Encoding.UTF8.GetBytes(corps);
            contexte.Response.StatusCode = (int)code;
            contexte.Response.ContentType = "application/json";
            contexte.Response.ContentLength64 = octets.Length;
            await contexte.Response.OutputStream.WriteAsync(octets);
            contexte.Response.Close();
        });
    }

    private FneApiClient Client(int delaiSecondes = 10)
    {
        var options = new FneApiOptions
        {
            BaseUrl = _adresse,
            ApiKey = "cle-de-test-123456789",
            TestAllowedUrls = [_adresse],
            TimeoutSeconds = delaiSecondes,
        };

        return new FneApiClient(
            new HttpClient { Timeout = TimeSpan.FromSeconds(delaiSecondes) },
            options,
            NullLogger<FneApiClient>.Instance);
    }

    private static FneInvoice Facture() => new()
    {
        InvoiceType = "sale",
        PaymentMethod = "deferred",
        Template = "B2B",
        ClientNcc = "1010983N",
        ClientCompanyName = "GEMS-CI",
        PointOfSale = "FISH-AFRIC",
        Establishment = "FISH-AFRIC",
        Items =
        [
            new FneInvoiceItem
            {
                Taxes = ["TVAB"],
                Reference = "P007",
                Description = "POITRINE DE POULET 10KG-AURA",
                Quantity = 40m,
                Amount = 2752.2936m,
                MeasurementUnit = "PKT",
            },
        ],
    };

    [Fact]
    public async Task Un_succes_rend_la_reference_et_le_jeton()
    {
        Repondre(HttpStatusCode.OK, """{"reference":"2304903U26000001052","token":"QR-XYZ"}""");

        var resultat = await Client().SignAsync(Facture());

        Assert.True(resultat.Reussi);
        Assert.Equal(200, resultat.CodeHttp);
        Assert.Equal("2304903U26000001052", resultat.ReferenceFne);
        Assert.Equal("QR-XYZ", resultat.Token);
    }

    [Fact]
    public async Task La_requete_porte_la_methode_le_chemin_et_la_cle()
    {
        Repondre(HttpStatusCode.OK, """{"reference":"REF"}""");

        await Client().SignAsync(Facture());

        Assert.Equal("POST", _methodeRecue);
        Assert.Equal("/ws/external/invoices/sign", _cheminRecu);
        Assert.Equal("Bearer cle-de-test-123456789", _enteteRecu);
    }

    [Fact]
    public async Task Le_corps_envoye_est_celui_de_la_facture()
    {
        Repondre(HttpStatusCode.OK, """{"reference":"REF"}""");

        await Client().SignAsync(Facture());

        using var recu = JsonDocument.Parse(_corpsRecu);
        var racine = recu.RootElement;

        Assert.Equal("FISH-AFRIC", racine.GetProperty("pointOfSale").GetString());
        Assert.Equal("1010983N", racine.GetProperty("clientNcc").GetString());
        Assert.Equal("", racine.GetProperty("clientEmail").GetString());

        var item = racine.GetProperty("items")[0];
        Assert.Equal(40m, item.GetProperty("quantity").GetDecimal());
        // Le prix part avec toutes ses décimales : c'est la plateforme qui
        // décidera de l'arrondi, et le contrôle ARRONDI_NON_TRANCHE l'annonce.
        Assert.Equal(2752.2936m, item.GetProperty("amount").GetDecimal());
        Assert.Equal("PKT", item.GetProperty("measurementUnit").GetString());
        Assert.Equal("TVAB", item.GetProperty("taxes")[0].GetString());
    }

    [Fact]
    public async Task Un_refus_de_la_plateforme_n_est_pas_un_succes()
    {
        Repondre(HttpStatusCode.UnprocessableEntity, """{"message":"clientEmail requis"}""");

        var resultat = await Client().SignAsync(Facture());

        Assert.False(resultat.Reussi);
        Assert.Equal(422, resultat.CodeHttp);
        Assert.Contains("clientEmail requis", resultat.CorpsBrut);
        Assert.Contains("422", resultat.Erreur);
    }

    [Fact]
    public async Task Une_reponse_acceptee_sans_reference_n_est_pas_un_succes()
    {
        // Le cas le plus dangereux : la DGI a peut-être enregistré la facture,
        // et nous ne saurions pas sous quel numéro.
        Repondre(HttpStatusCode.OK, """{"status":"ok"}""");

        var resultat = await Client().SignAsync(Facture());

        Assert.False(resultat.Reussi);
        Assert.Equal(200, resultat.CodeHttp);
        Assert.Contains("peut-être certifiée", resultat.Erreur);
        Assert.Equal("""{"status":"ok"}""", resultat.CorpsBrut);
    }

    [Fact]
    public async Task Une_plateforme_injoignable_se_dit_sans_lever()
    {
        var options = new FneApiOptions
        {
            BaseUrl = "http://localhost:1/ws",
            ApiKey = "k",
            TestAllowedUrls = ["http://localhost:1/ws"],
        };
        var client = new FneApiClient(
            new HttpClient(), options, NullLogger<FneApiClient>.Instance);

        var resultat = await client.SignAsync(Facture());

        Assert.False(resultat.Reussi);
        Assert.Null(resultat.CodeHttp);
        Assert.Contains("injoignable", resultat.Erreur);
    }

    [Fact]
    public void La_requete_decrite_ne_montre_jamais_la_cle()
    {
        var description = Client().DecrireRequete(Facture());

        Assert.DoesNotContain("cle-de-test-123456789", description);
        Assert.Contains("•", description);
        Assert.Contains("POST", description);
        Assert.Contains("/ws/external/invoices/sign", description);
    }
}
