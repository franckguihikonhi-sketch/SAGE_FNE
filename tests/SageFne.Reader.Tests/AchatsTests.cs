using System.Text.Json;
using Microsoft.Extensions.Options;
using SageFne.Core.Batch;
using SageFne.Core.Certification;
using SageFne.Core.Configuration;
using SageFne.Core.Data;
using SageFne.Core.Mapping;
using SageFne.Core.Models.Fne;
using SageFne.Core.Models.Sage;
using SageFne.Core.Validation;

namespace SageFne.Core.Tests;

/// <summary>
/// Le bordereau d'achat : un autre document, pas une vente inversée.
/// </summary>
/// <remarks>
/// Ce que la DGI appelle <c>purchase</c> est le bordereau d'achat de produits
/// agricoles. Son tableau de paramètres ne porte ni <c>taxes</c> ni
/// <c>clientNcc</c> — un producteur n'a pas de NCC, et l'achat qu'on lui fait
/// ne porte pas de TVA. Appliquer les règles de la vente bloquerait tous les
/// achats sur des exigences qui ne les concernent pas.
/// </remarks>
public class AchatsTests
{
    private static InvoiceBatchReader Lecteur()
    {
        var reglages = ReglagesDEssai.SansDelaiPortail;
        return new InvoiceBatchReader(
            new DemoSageInvoiceRepository(estReel: true),
            new FneInvoiceMapper(reglages),
            new DemoCertificationLedger(),
            reglages);
    }

    private static async Task<InvoiceConversion> AchatAsync(string piece)
    {
        var lot = await Lecteur().ReadAsync(InvoiceQuery.PieceAchat(piece));
        return Assert.Single(lot.Conversions);
    }

    [Fact]
    public async Task Un_achat_n_est_pas_lu_par_le_domaine_des_ventes()
    {
        // Le domaine ne s'élargit jamais par accident : l'appelant demande
        // l'achat, il ne le reçoit pas en prime d'une lecture de ventes.
        var ventes = await Lecteur().ReadAsync(new InvoiceQuery { Limite = 500 });

        Assert.DoesNotContain(ventes.Conversions, c => c.Header.Piece == "AC001");
        Assert.All(ventes.Conversions, c => Assert.Equal(SageDomaines.Vente, c.Header.Domaine));
    }

    [Fact]
    public async Task Un_achat_part_en_purchase()
    {
        var conversion = await AchatAsync("AC001");

        Assert.NotNull(conversion.Invoice);
        Assert.Equal("purchase", conversion.Invoice!.InvoiceType);
    }

    [Fact]
    public async Task Un_achat_ne_porte_aucune_taxe_dans_le_corps_envoye()
    {
        // Et le champ est absent, pas vide : un tableau vide affirme « aucune
        // taxe », l'absence de champ n'affirme rien. Le tableau des paramètres
        // du bordereau d'achat ne mentionne pas « taxes ».
        var conversion = await AchatAsync("AC001");
        var json = JsonSerializer.Serialize(conversion.Invoice);

        Assert.DoesNotContain("\"taxes\"", json);
        Assert.DoesNotContain("\"customTaxes\"", json);
    }

    [Fact]
    public async Task Une_vente_continue_de_porter_ses_taxes()
    {
        // Le pendant du précédent : sans lui, supprimer les taxes partout
        // passerait pour une réussite. Les ventes certifient en production.
        var lot = await Lecteur().ReadAsync(InvoiceQuery.Piece("1220"));
        var json = JsonSerializer.Serialize(lot.Conversions.Single().Invoice);

        Assert.Contains("\"taxes\"", json);
        Assert.Contains("TVA", json);
    }

    [Fact]
    public async Task Un_fournisseur_sans_NCC_ne_bloque_pas_l_achat()
    {
        // La règle qui aurait tout bloqué. Le NCC est obligatoire en B2B pour
        // une vente ; sur un bordereau d'achat, le fournisseur est un
        // producteur, qui n'en a pas.
        var conversion = await AchatAsync("AC001");

        Assert.DoesNotContain(conversion.Report.Constats, c => c.Code == "NCC_MANQUANT");
        Assert.Equal("", conversion.Invoice!.ClientNcc);
    }

    [Fact]
    public async Task Une_absence_de_TVA_ne_bloque_pas_l_achat()
    {
        // TVA_ABSENTE bloque une vente, et doit le faire. Sur un achat, une
        // ligne sans taxe est la normale, pas un défaut.
        var conversion = await AchatAsync("AC001");

        Assert.DoesNotContain(conversion.Report.Constats, c => c.Code == "TVA_ABSENTE");
        Assert.DoesNotContain(
            conversion.Report.Constats, c => c.Code == "ZERO_VAT_CATEGORY_UNKNOWN");
    }

    [Fact]
    public async Task Un_achat_annonce_ce_qu_il_declare()
    {
        // Certifier en « purchase », c'est affirmer un bordereau d'achat de
        // produits agricoles. Ce n'est pas vérifiable depuis Sage : la pièce le
        // dit, et l'écran le répète avant le clic.
        var conversion = await AchatAsync("AC001");

        Assert.Contains(
            conversion.Report.Constats,
            c => c.Code == "ACHAT_BORDEREAU_DECLARE");
    }

    [Fact]
    public async Task L_identite_d_un_achat_ne_peut_pas_heurter_celle_d_une_vente()
    {
        // Le domaine entre dans l'identité : une vente 99 et un achat 99 sont
        // deux pièces distinctes au registre. Sans cela, certifier l'une
        // interdirait l'autre pour toujours.
        var achat = await AchatAsync("AC001");

        Assert.StartsWith("1/", achat.Header.Identite);
        Assert.NotEqual("0/", achat.Header.Identite[..2]);
    }

    [Fact]
    public async Task La_completude_ne_plante_pas_sur_un_achat()
    {
        // « taxes » est absent du bordereau, donc null sur les items. Tout
        // lecteur qui le déréférence sans condition lève une
        // NullReferenceException sur la première pièce d'achat venue — ce que
        // faisait FneCompleteness, et que le compilateur signalait en CS8602.
        var achat = await AchatAsync("AC001");

        var manques = FneCompleteness.Verifier(achat.Invoice!, "B2B");

        Assert.DoesNotContain(manques, manque => manque.Champ.EndsWith(".taxes"));
    }

    [Fact]
    public async Task Un_achat_n_est_pas_bloque_sur_un_NCC_qu_il_n_envoie_pas()
    {
        // L'API n° 3 n'a pas de clientNcc. L'exiger en B2B bloquerait tout le
        // domaine des achats sur un champ absent du corps de requête.
        var achat = await AchatAsync("AC001");

        Assert.DoesNotContain(
            FneCompleteness.Verifier(achat.Invoice!, "B2B"),
            manque => manque.Champ == "clientNcc");
    }

    [Fact]
    public void Une_vente_reste_bloquee_quand_la_ligne_n_a_aucune_taxe()
    {
        // Le pendant du test précédent : la tolérance vaut pour l'achat seul.
        var vente = new FneInvoice
        {
            InvoiceType = TypesFactureFne.Vente,
            PointOfSale = "FISH-AFRIC",
            Establishment = "FISH-AFRIC",
            ClientCompanyName = "GEMS-CI",
            ClientNcc = "1010983N",
            ClientPhone = "0700000000",
            Items = [new FneInvoiceItem
            {
                Reference = "ART1", Description = "Article",
                Quantity = 1m, Amount = 1000m, Taxes = [],
            }],
        };

        Assert.Contains(
            FneCompleteness.Verifier(vente, "B2B"),
            manque => manque.Champ == "items[0].taxes");
    }

    [Fact]
    public async Task La_recherche_de_candidats_ne_plante_pas_sur_un_achat()
    {
        // « customTaxes » est absent lui aussi. La recherche du régime zéro
        // porte sur les ventes, mais rien ne l'empêche de recevoir un achat :
        // elle doit le traverser sans lever, pas s'y fier.
        var achat = await AchatAsync("AC001");

        var candidat = CandidatFne.Evaluer(achat, TauxRecherche.Normal, 1m);

        Assert.NotNull(candidat);
    }

    [Fact]
    public async Task Les_lignes_d_un_achat_gardent_leurs_montants()
    {
        var conversion = await AchatAsync("AC001");

        // 200 × 2 000 + 50 × 1 000 = 450 000
        Assert.Equal(450000m, conversion.TotalHT);
        Assert.Equal(2, conversion.Invoice!.Items.Count);
        Assert.Equal(2000m, conversion.Invoice.Items[0].Amount);
        Assert.Equal(200m, conversion.Invoice.Items[0].Quantity);
    }
}
