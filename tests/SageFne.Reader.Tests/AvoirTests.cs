using Microsoft.Extensions.Logging.Abstractions;
using SageFne.Core.Batch;
using SageFne.Core.Certification;
using SageFne.Core.Configuration;
using SageFne.Core.Data;
using SageFne.Core.Fne;
using SageFne.Core.Mapping;
using SageFne.Core.Models.Fne;
using SageFne.Reader.Batch;

namespace SageFne.Core.Tests;

/// <summary>
/// L'avoir : la seule réponse à une facture certifiée à tort.
/// </summary>
/// <remarks>
/// Il est né d'un fait, pas d'une prévision : la pièce 1225 a été certifiée
/// deux fois, sous 2304903U26000000002 puis 2304903U26000000013, parce que le
/// CLI et le service tenaient chacun leur registre et qu'aucun ne voyait
/// l'autre.
///
/// Rien de ce que l'avoir envoie ne vient de Sage. La procédure de la DGI est
/// explicite : l'identifiant de l'URL « doit être récupéré dans la réponse de
/// la requête de certification », et chaque ligne porte l'« id » que la
/// plateforme lui a attribué.
/// </remarks>
public class AvoirTests
{
    /// <summary>Une réponse de certification conforme à la procédure DGI, page 12.</summary>
    private const string ReponseDgi = """
        {
          "ncc": "9606123E",
          "reference": "9606123E25000000019",
          "token": "http://54.247.95.108/fr/verification/019465c1-3f61-766c-9652-706e32dfb436",
          "warning": false,
          "balance_sticker": 179,
          "invoice": {
            "id": "e2b2d8da-a532-4c08-9182-f5b428ca468d",
            "reference": "9606123E25000000019",
            "items": [
              { "id": "bf9cc241-9b5f-4d26-a570-aa8e682a759e", "quantity": 30,
                "reference": "ref009", "description": "sac de riz Dinor 5 x 5", "amount": 20000 },
              { "id": "50b5c9d9-e22d-4dce-ba3c-5d2519c3418f", "quantity": 20,
                "reference": "ref001", "description": "Huile lesieur 5 litres", "amount": 12000 }
            ]
          }
        }
        """;

    // --- Ce que la réponse conservée permet de lire -------------------------

    [Fact]
    public void L_identifiant_de_la_facture_vient_de_la_reponse_conservee()
    {
        var lecture = AvoirFne.Lire(ReponseDgi);

        Assert.True(lecture.Possible);
        Assert.Equal("e2b2d8da-a532-4c08-9182-f5b428ca468d", lecture.IdFacture);
    }

    [Fact]
    public void Chaque_ligne_porte_l_identifiant_attribue_par_la_DGI()
    {
        var lecture = AvoirFne.Lire(ReponseDgi);

        Assert.Equal(2, lecture.Lignes.Count);
        Assert.Equal("bf9cc241-9b5f-4d26-a570-aa8e682a759e", lecture.Lignes[0].Id);
        Assert.Equal(30m, lecture.Lignes[0].Quantite);
        Assert.Equal("ref009", lecture.Lignes[0].Reference);
        Assert.Equal("sac de riz Dinor 5 x 5", lecture.Lignes[0].Designation);
    }

    [Theory]
    [InlineData("", "aucune réponse")]
    [InlineData("pas du json", "n'est pas du JSON")]
    [InlineData("""{"reference":"X"}""", "invoice")]
    [InlineData("""{"invoice":{"items":[]}}""", "invoice.id")]
    [InlineData("""{"invoice":{"id":"a"}}""", "invoice.items")]
    [InlineData("""{"invoice":{"id":"a","items":[]}}""", "aucune ligne")]
    public void Une_reponse_qui_ne_porte_pas_les_identifiants_ne_donne_aucun_avoir(
        string corps, string attendu)
    {
        var lecture = AvoirFne.Lire(corps);

        Assert.False(lecture.Possible);
        Assert.Contains(attendu, lecture.Empechement);
    }

    [Fact]
    public void Une_ligne_sans_identifiant_arrete_tout_l_avoir()
    {
        // Un avoir sur les seules lignes identifiables serait un avoir faux :
        // il annulerait moins que ce que l'exploitant a demandé, sans le dire.
        var lecture = AvoirFne.Lire("""
            {"invoice":{"id":"a","items":[
              {"id":"x","quantity":1},
              {"quantity":2}
            ]}}
            """);

        Assert.False(lecture.Possible);
        Assert.Contains("ligne 1", lecture.Empechement);
    }

    // --- Le corps envoyé ----------------------------------------------------

    [Fact]
    public void Sans_precision_l_avoir_rend_tout()
    {
        var corps = AvoirFne.Corps(AvoirFne.Lire(ReponseDgi).Lignes);

        Assert.Equal(2, corps.Items.Count);
        Assert.Equal(30m, corps.Items[0].Quantity);
        Assert.Equal(20m, corps.Items[1].Quantity);
    }

    [Fact]
    public void Un_avoir_partiel_ne_touche_que_la_ligne_nommee()
    {
        var lignes = AvoirFne.Lire(ReponseDgi).Lignes;

        var corps = AvoirFne.Corps(lignes, new Dictionary<string, decimal> { ["ref009"] = 5m });

        Assert.Equal(5m, corps.Items[0].Quantity);
        Assert.Equal(20m, corps.Items[1].Quantity);
    }

    [Fact]
    public void Une_ligne_ramenee_a_zero_ne_part_pas()
    {
        var lignes = AvoirFne.Lire(ReponseDgi).Lignes;

        var corps = AvoirFne.Corps(lignes, new Dictionary<string, decimal> { ["ref009"] = 0m });

        Assert.Equal("50b5c9d9-e22d-4dce-ba3c-5d2519c3418f", Assert.Single(corps.Items).Id);
    }

    [Fact]
    public void Le_corps_serialise_ne_porte_que_id_et_quantity()
    {
        // Le tableau des paramètres de la procédure n'en comporte pas d'autres.
        var json = System.Text.Json.JsonSerializer.Serialize(
            AvoirFne.Corps(AvoirFne.Lire(ReponseDgi).Lignes));

        Assert.Equal(
            """{"items":[{"id":"bf9cc241-9b5f-4d26-a570-aa8e682a759e","quantity":30},"""
            + """{"id":"50b5c9d9-e22d-4dce-ba3c-5d2519c3418f","quantity":20}]}""",
            json);
    }

    // --- L'adresse ----------------------------------------------------------

    [Fact]
    public void L_adresse_de_l_avoir_porte_l_identifiant_de_la_facture()
    {
        var reglages = new FneApiOptions { BaseUrl = "http://54.247.95.108/ws" };

        Assert.Equal(
            "http://54.247.95.108/ws/external/invoices/e2b2d8da-a532-4c08-9182-f5b428ca468d/refund",
            reglages.AdresseAvoir("e2b2d8da-a532-4c08-9182-f5b428ca468d").ToString());
    }

    [Fact]
    public void Un_identifiant_inattendu_ne_deforme_pas_l_adresse()
    {
        // L'identifiant vient de la plateforme, pas de nous : il est encodé.
        var reglages = new FneApiOptions { BaseUrl = "http://54.247.95.108/ws" };

        Assert.EndsWith(
            "/external/invoices/a%2F..%2Fsign/refund",
            reglages.AdresseAvoir("a/../sign").ToString());
    }

    // --- La commande --------------------------------------------------------

    [Fact]
    public void Avoir_ecrit_au_registre_et_refuse_donc_un_registre_ambigu() =>
        Assert.True(Verbes.EcritAuRegistre(Verbe.Avoir));

    [Fact]
    public void L_avoir_se_demande_par_reference_et_quantite()
    {
        var ligne = CommandLine.Parse(["avoir", "1225", "--ligne", "ART1=3"]);

        Assert.Empty(ligne.Erreurs);
        Assert.Equal(Verbe.Avoir, ligne.Verbe);
        Assert.Equal(3m, ligne.Articles["ART1"]);
    }

    [Theory]
    [InlineData("ART1")]
    [InlineData("ART1=")]
    [InlineData("ART1=deux")]
    [InlineData("ART1=-1")]
    public void Une_quantite_qui_n_en_est_pas_une_est_refusee(string couple)
    {
        var ligne = CommandLine.Parse(["avoir", "1225", "--ligne", couple]);

        Assert.NotEmpty(ligne.Erreurs);
    }

    [Fact]
    public void Sans_confirmer_rien_ne_part()
    {
        var ligne = CommandLine.Parse(["avoir", "1225"]);

        Assert.False(ligne.Confirme);
    }

    // --- L'opération, registre compris --------------------------------------

    private const string Piece = "1221";
    private const string Identite = "0/6/1221";

    private sealed class Registre(CertifiedInvoice? entree) : ICertificationLedger
    {
        private readonly Dictionary<string, CertifiedInvoice> _entrees =
            entree is null ? [] : new() { [entree.Identite] = entree };

        public List<CertifiedInvoice> Ecritures { get; } = [];

        public Task<IReadOnlyDictionary<string, CertifiedInvoice>> LookupAsync(
            IReadOnlyCollection<string> identites, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, CertifiedInvoice>>(
                identites.Where(_entrees.ContainsKey).ToDictionary(i => i, i => _entrees[i]));

        public Task RecordAsync(CertifiedInvoice certification, CancellationToken ct = default)
        {
            Ecritures.Add(certification);
            _entrees[certification.Identite] = certification;
            return Task.CompletedTask;
        }
    }

    /// <summary>Une doublure qui sait signer ET rembourser, comme le client réel.</summary>
    private sealed class ClientAvoir(FneSignResult reponse) : IFneApiClient, IFneAvoirClient
    {
        public string? IdAppele { get; private set; }
        public CorpsAvoir? CorpsAppele { get; private set; }
        public bool Reel => false;
        public string DecrireRequete(FneInvoice facture) => "POST …";
        public string DecrireAvoir(string idFacture, CorpsAvoir corps) => $"POST …/{idFacture}/refund";

        public Task<FneSignResult> SignAsync(FneInvoice facture, CancellationToken ct = default) =>
            throw new InvalidOperationException("un avoir ne signe pas.");

        public Task<FneSignResult> RembourserAsync(
            string idFacture, CorpsAvoir corps, CancellationToken ct = default)
        {
            IdAppele = idFacture;
            CorpsAppele = corps;
            return Task.FromResult(reponse);
        }
    }

    /// <summary>Une doublure qui ne sait que signer, comme les seize autres.</summary>
    private sealed class ClientSansAvoir : IFneApiClient
    {
        public bool Reel => false;
        public string DecrireRequete(FneInvoice facture) => "POST …";

        public Task<FneSignResult> SignAsync(FneInvoice facture, CancellationToken ct = default) =>
            Task.FromResult(new FneSignResult(true, 201, "REF"));
    }

    private static CertifiedInvoice Certifiee(string reponse = ReponseDgi) => new()
    {
        Identite = Identite,
        Piece = Piece,
        Empreinte = "peu importe",
        Etat = EtatFne.Certified,
        ReferenceFne = "2304903U26000000002",
        Reponse = reponse,
        CertifieeLe = DateTimeOffset.Now.AddHours(-1),
    };

    private static (InvoiceSender Expediteur, Registre Registre) Monter(
        CertifiedInvoice? entree, IFneApiClient client)
    {
        var registre = new Registre(entree);
        var reglages = ReglagesDEssai.SansDelaiPortail;
        var lecteur = new InvoiceBatchReader(
            new DemoSageInvoiceRepository(estReel: true),
            new FneInvoiceMapper(reglages), registre, reglages);

        return (new InvoiceSender(
            lecteur, registre, client, NullLogger<InvoiceSender>.Instance, reglages), registre);
    }

    [Fact]
    public async Task Sans_confirmer_aucune_requete_ne_part()
    {
        var client = new ClientAvoir(new FneSignResult(true, 201, "AVOIR-1"));
        var (expediteur, registre) = Monter(Certifiee(), client);

        var resultat = await expediteur.AvoirAsync(Piece, confirme: false);

        Assert.True(resultat.ConfirmationManque);
        Assert.Null(client.IdAppele);
        Assert.Empty(registre.Ecritures);
    }

    [Fact]
    public async Task L_avoir_part_avec_les_identifiants_de_la_DGI()
    {
        var client = new ClientAvoir(new FneSignResult(true, 201, "AVOIR-1"));
        var (expediteur, _) = Monter(Certifiee(), client);

        var resultat = await expediteur.AvoirAsync(Piece, confirme: true);

        Assert.True(resultat.Applique);
        Assert.Equal("e2b2d8da-a532-4c08-9182-f5b428ca468d", client.IdAppele);
        Assert.Equal("bf9cc241-9b5f-4d26-a570-aa8e682a759e", client.CorpsAppele!.Items[0].Id);
    }

    [Fact]
    public async Task La_trace_est_ecrite_avant_l_appel()
    {
        // Même discipline que l'envoi : un avoir dont la réponse se perd doit
        // avoir laissé une trace, sans quoi un second avoir partirait.
        var client = new ClientAvoir(new FneSignResult(false, null, Erreur: "délai dépassé."));
        var (expediteur, registre) = Monter(Certifiee(), client);

        await expediteur.AvoirAsync(Piece, confirme: true);

        Assert.Equal(2, registre.Ecritures.Count);
        Assert.Contains(registre.Ecritures[0].Tentatives, t => t.Genre == GenreTentative.Avoir);
        Assert.Contains("Issue inconnue", registre.Ecritures[^1].Motif + registre.Ecritures[^1]
            .Tentatives[^1].Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task La_piece_reste_certifiee_apres_l_avoir()
    {
        // Un avoir ne défait pas la certification : il lui répond. Repasser la
        // pièce en « pas certifiée » rouvrirait la porte au renvoi.
        var client = new ClientAvoir(new FneSignResult(true, 201, "AVOIR-1"));
        var (expediteur, registre) = Monter(Certifiee(), client);

        await expediteur.AvoirAsync(Piece, confirme: true);

        Assert.Equal(EtatFne.Certified, registre.Ecritures[^1].Etat);
        Assert.Equal("2304903U26000000002", registre.Ecritures[^1].ReferenceFne);
    }

    [Fact]
    public async Task La_reponse_de_certification_n_est_jamais_ecrasee()
    {
        // C'est elle qui porte les identifiants DGI. L'écraser par la réponse
        // de l'avoir rendrait tout second avoir impossible.
        var client = new ClientAvoir(new FneSignResult(true, 201, "AVOIR-1", CorpsBrut: "{\"autre\":1}"));
        var (expediteur, registre) = Monter(Certifiee(), client);

        await expediteur.AvoirAsync(Piece, confirme: true);

        Assert.Contains("e2b2d8da", registre.Ecritures[^1].Reponse);
    }

    [Fact]
    public async Task Une_piece_non_certifiee_n_a_rien_a_annuler()
    {
        var client = new ClientAvoir(new FneSignResult(true, 201, "AVOIR-1"));
        var (expediteur, _) = Monter(null, client);

        var resultat = await expediteur.AvoirAsync(Piece, confirme: true);

        Assert.False(resultat.Applique);
        Assert.Null(client.IdAppele);
        Assert.Contains("pas certifiée", resultat.Message);
    }

    [Fact]
    public async Task Une_reponse_sans_identifiants_arrete_avant_tout_appel()
    {
        var client = new ClientAvoir(new FneSignResult(true, 201, "AVOIR-1"));
        var (expediteur, registre) = Monter(Certifiee(reponse: """{"reference":"X"}"""), client);

        var resultat = await expediteur.AvoirAsync(Piece, confirme: true);

        Assert.False(resultat.Applique);
        Assert.Null(client.IdAppele);
        Assert.Empty(registre.Ecritures);
    }

    [Fact]
    public async Task Une_reference_inconnue_arrete_l_avoir()
    {
        var client = new ClientAvoir(new FneSignResult(true, 201, "AVOIR-1"));
        var (expediteur, _) = Monter(Certifiee(), client);

        var resultat = await expediteur.AvoirAsync(
            Piece, confirme: true, new Dictionary<string, decimal> { ["INCONNUE"] = 1m });

        Assert.False(resultat.Applique);
        Assert.Null(client.IdAppele);
        Assert.Contains("INCONNUE", resultat.Message);
    }

    [Fact]
    public async Task On_ne_rend_pas_plus_que_ce_qui_a_ete_certifie()
    {
        var client = new ClientAvoir(new FneSignResult(true, 201, "AVOIR-1"));
        var (expediteur, _) = Monter(Certifiee(), client);

        var resultat = await expediteur.AvoirAsync(
            Piece, confirme: true, new Dictionary<string, decimal> { ["ref009"] = 31m });

        Assert.False(resultat.Applique);
        Assert.Null(client.IdAppele);
        Assert.Contains("31 > 30", resultat.Message);
    }

    [Fact]
    public async Task Un_client_qui_ne_sait_pas_rembourser_le_dit()
    {
        var (expediteur, registre) = Monter(Certifiee(), new ClientSansAvoir());

        var resultat = await expediteur.AvoirAsync(Piece, confirme: true);

        Assert.False(resultat.Applique);
        Assert.Empty(registre.Ecritures);
    }
}
