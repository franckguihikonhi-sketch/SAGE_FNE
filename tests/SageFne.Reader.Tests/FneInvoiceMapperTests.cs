using Microsoft.Extensions.Options;
using SageFne.Reader.Configuration;
using SageFne.Reader.Mapping;
using SageFne.Reader.Models.Fne;
using SageFne.Reader.Models.Sage;
using SageFne.Reader.Validation;

namespace SageFne.Reader.Tests;

/// <summary>
/// Le mapping des taxes est le point où une erreur coûte le plus cher : une
/// TVA fausse est certifiée par la DGI et ne se rattrape plus. Ces tests
/// fixent la règle sur laquelle tout le reste s'appuie.
/// </summary>
public class FneInvoiceMapperTests
{
    private static readonly SageDocumentHeader Entete = new()
    {
        Domaine = 0,
        Type = 6,
        Piece = "1219",
        Date = new DateTime(2025, 12, 3),
        Tiers = "4111SITASARL",
        TotalTTC = 498339.625m,
    };

    private static readonly SageCustomer Client = new()
    {
        CtNum = "4111SITASARL",
        Intitule = "SITA SARL",
        Identifiant = "14322625",
        Pays = "COTE D'IVOIRE",
    };

    private static FneInvoiceMapper Mappeur() =>
        new(Options.Create(new FneOptions
        {
            PointOfSale = "SIEGE",
            Establishment = "PRINCIPAL",
            PaymentMethod = "deferred",
            Template = "B2B",
        }));

    private static SageDocumentLine Ligne(
        decimal taxe1 = 0m,
        string code1 = "",
        decimal taxe2 = 0m,
        string code2 = "",
        decimal taxe3 = 0m,
        string code3 = "") => new()
    {
        Domaine = 0,
        Type = 6,
        Piece = "1219",
        Ligne = 1,
        ArticleReference = "13415001",
        Designation = "Queue De Boeuf PV - Friboi",
        Quantite = 196.39m,
        PrixUnitaire = 2500m,
        Unite = "KG",
        MontantHT = 490975m,
        Taxe1 = taxe1,
        CodeTaxe1 = code1,
        Taxe2 = taxe2,
        CodeTaxe2 = code2,
        Taxe3 = taxe3,
        CodeTaxe3 = code3,
    };

    private static FneInvoiceItem Premier(SageDocumentLine ligne) =>
        Mappeur().Map(Entete, [ligne], Client).Items.Single();

    [Fact]
    public void Tva_a_18_pourcent_donne_le_code_TVA()
    {
        var article = Premier(Ligne(taxe1: 18m, code1: "TVA"));

        Assert.Equal(["TVA"], article.Taxes);
        Assert.Empty(article.CustomTaxes);
    }

    [Fact]
    public void Tva_a_9_pourcent_donne_le_code_TVAB()
    {
        // Le dossier code ce taux « TVA » comme le taux normal : seul le taux
        // porté par la ligne permet de les distinguer.
        var article = Premier(Ligne(taxe1: 9m, code1: "TVA"));

        Assert.Equal(["TVAB"], article.Taxes);
    }

    [Fact]
    public void Airsi_part_en_customTaxes_et_pas_en_taxes()
    {
        var article = Premier(Ligne(taxe2: 1.5m, code2: "AIRSI"));

        Assert.Empty(article.Taxes);
        var prelevement = Assert.Single(article.CustomTaxes);
        Assert.Equal("AIRSI", prelevement.Name);
        Assert.Equal(1.5m, prelevement.Amount);
    }

    [Fact]
    public void Sans_taxe_aucun_code_de_tva_n_est_invente()
    {
        var article = Premier(Ligne());

        Assert.Empty(article.Taxes);
        Assert.Empty(article.CustomTaxes);
    }

    [Fact]
    public void Tva_18_et_airsi_se_rangent_chacune_de_son_cote()
    {
        var article = Premier(Ligne(taxe1: 18m, code1: "TVA", taxe2: 1.5m, code2: "AIRSI"));

        Assert.Equal(["TVA"], article.Taxes);
        Assert.Equal(1.5m, Assert.Single(article.CustomTaxes).Amount);
    }

    [Fact]
    public void Tva_9_et_airsi_se_rangent_chacune_de_son_cote()
    {
        var article = Premier(Ligne(taxe1: 9m, code1: "TVA", taxe2: 1.5m, code2: "AIRSI"));

        Assert.Equal(["TVAB"], article.Taxes);
        Assert.Equal("AIRSI", Assert.Single(article.CustomTaxes).Name);
    }

    [Fact]
    public void Airsi_est_reconnue_quel_que_soit_son_emplacement()
    {
        // Rien ne garantit que le dossier gardera l'AIRSI en position 2.
        var enPremier = Premier(Ligne(taxe1: 1.5m, code1: "AIRSI", taxe2: 18m, code2: "TVA"));
        var enTroisieme = Premier(Ligne(taxe1: 18m, code1: "TVA", taxe3: 1.5m, code3: "airsi"));

        Assert.Equal(["TVA"], enPremier.Taxes);
        Assert.Equal("AIRSI", Assert.Single(enPremier.CustomTaxes).Name);
        Assert.Equal(["TVA"], enTroisieme.Taxes);
        Assert.Equal("AIRSI", Assert.Single(enTroisieme.CustomTaxes).Name);
    }

    [Fact]
    public void Un_taux_hors_nomenclature_est_signale_et_non_repris()
    {
        var rapport = new CheckReport();
        Mappeur().Map(Entete, [Ligne(taxe1: 12m, code1: "TVA")], Client, rapport);

        Assert.Contains(rapport.Constats, constat => constat.Code == "TAUX_HORS_NOMENCLATURE");
        Assert.DoesNotContain(rapport.Constats, constat => constat.Severite == Severite.Erreur);
    }

    [Fact]
    public void L_entete_de_la_facture_reprend_le_client_et_le_parametrage()
    {
        var facture = Mappeur().Map(Entete, [Ligne(taxe2: 1.5m, code2: "AIRSI")], Client);

        Assert.Equal("sale", facture.InvoiceType);
        Assert.Equal("deferred", facture.PaymentMethod);
        Assert.Equal("B2B", facture.Template);
        Assert.False(facture.IsRne);
        Assert.Equal("14322625", facture.ClientNcc);
        Assert.Equal("SITA SARL", facture.ClientCompanyName);
        Assert.Equal("SIEGE", facture.PointOfSale);
        Assert.Equal("PRINCIPAL", facture.Establishment);
        Assert.Equal(0m, facture.Discount);
    }

    [Fact]
    public void La_ligne_porte_le_prix_unitaire_et_non_le_montant()
    {
        var article = Premier(Ligne(taxe2: 1.5m, code2: "AIRSI"));

        Assert.Equal("13415001", article.Reference);
        Assert.Equal("Queue De Boeuf PV - Friboi", article.Description);
        Assert.Equal(196.39m, article.Quantity);
        Assert.Equal(2500m, article.Amount);
        Assert.Equal("KG", article.MeasurementUnit);
        Assert.Equal(0m, article.Discount);
    }
}
