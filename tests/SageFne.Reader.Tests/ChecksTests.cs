using SageFne.Core.Models.Sage;
using SageFne.Core.Validation;

namespace SageFne.Core.Tests;

public class ChecksTests
{
    private static SageDocumentLine Ligne(decimal qte, decimal prix, decimal ht, decimal ttc, decimal airsi = 0m) =>
        new()
        {
            Domaine = 0,
            Type = 6,
            Piece = "1219",
            Ligne = 1,
            Designation = "Queue De Boeuf PV - Friboi",
            ArticleReference = "13415001",
            Quantite = qte,
            PrixUnitaire = prix,
            MontantHT = ht,
            MontantTTC = ttc,
            Taxe2 = airsi,
            CodeTaxe2 = airsi > 0m ? "AIRSI" : "",
        };

    [Fact]
    public void La_facture_reelle_passe_les_controles_financiers()
    {
        // 196,39 x 2 500 = 490 975 ; AIRSI 1,5 % = 7 364,625 ; TTC = 498 339,625.
        var rapport = new CheckReport();
        FinancialChecks.Run([Ligne(196.39m, 2500m, 490975m, 498339.625m, 1.5m)], rapport);

        Assert.Empty(rapport.Constats);
    }

    [Fact]
    public void Un_ecart_de_HT_est_signale_sans_bloquer()
    {
        var rapport = new CheckReport();
        FinancialChecks.Run([Ligne(196.39m, 2500m, 480000m, 487200m)], rapport);

        Assert.Contains(rapport.Constats, constat => constat.Code == "ECART_HT");
        Assert.False(rapport.ContientDesErreurs);
    }

    [Fact]
    public void Un_centime_d_arrondi_ne_declenche_rien()
    {
        var rapport = new CheckReport();
        FinancialChecks.Run([Ligne(196.39m, 2500m, 490975.40m, 498339.625m, 1.5m)], rapport);

        Assert.Empty(rapport.Constats);
    }

    [Fact]
    public void Un_entete_a_zero_est_signale_mais_les_lignes_font_foi()
    {
        var entete = new SageDocumentHeader
        {
            Domaine = 0, Type = 6, Piece = "1219",
            Date = new DateTime(2025, 12, 3), Tiers = "4111SITASARL", TotalHT = 0m,
        };
        var rapport = new CheckReport();

        FinancialChecks.CompareHeader(entete, [Ligne(196.39m, 2500m, 490975m, 498339.625m, 1.5m)], rapport);

        var constat = Assert.Single(rapport.Constats);
        Assert.Equal("ENTETE_HT_NUL", constat.Code);
        Assert.Equal(Severite.Avertissement, constat.Severite);
    }

    [Fact]
    public void Le_NCC_est_exige_en_B2B()
    {
        var entete = new SageDocumentHeader
        {
            Domaine = 0, Type = 6, Piece = "1219",
            Date = new DateTime(2025, 12, 3), Tiers = "4111SITASARL",
        };
        var client = new SageCustomer { CtNum = "4111SITASARL", Intitule = "SITA SARL", Identifiant = "" };
        var rapport = new CheckReport();

        InvoiceValidator.Validate(entete, client, [Ligne(1m, 100m, 100m, 100m)], "B2B", rapport);

        Assert.Contains(rapport.Constats, constat => constat.Code == "NCC_MANQUANT");
        Assert.True(rapport.ContientDesErreurs);
    }

    [Fact]
    public void Une_quantite_nulle_est_une_erreur()
    {
        var entete = new SageDocumentHeader
        {
            Domaine = 0, Type = 6, Piece = "1219",
            Date = new DateTime(2025, 12, 3), Tiers = "4111SITASARL",
        };
        var client = new SageCustomer { CtNum = "4111SITASARL", Intitule = "SITA SARL", Identifiant = "14322625" };
        var rapport = new CheckReport();

        InvoiceValidator.Validate(entete, client, [Ligne(0m, 100m, 0m, 0m)], "B2B", rapport);

        Assert.Contains(rapport.Constats, constat => constat.Code == "QUANTITE_INVALIDE");
    }
}

/// <summary>
/// FNE ne reçoit pas le montant d'une ligne : il reçoit une quantité et un prix
/// unitaire, et refait la multiplication. Le franc CFA n'ayant pas de centimes,
/// un produit à décimales expose à une règle d'arrondi que la DGI ne publie pas.
/// </summary>
public class ArrondiTests
{
    private static SageDocumentLine Ligne(decimal quantite, decimal prixUnitaire, decimal montantHT) => new()
    {
        Domaine = 0, Type = 7, Piece = "1052", Ligne = 1000,
        ArticleReference = "P007", Designation = "POITRINE DE POULET 10KG-AURA",
        Quantite = quantite, PrixUnitaire = prixUnitaire,
        MontantHT = montantHT, MontantTTC = montantHT * 1.09m,
        Taxe1 = 9m, CodeTaxe1 = "TVA",
    };

    private static CheckReport Controler(SageDocumentLine ligne)
    {
        var rapport = new CheckReport();
        FinancialChecks.Run([ligne], rapport);
        return rapport;
    }

    [Fact]
    public void Un_produit_a_decimales_est_signale()
    {
        // Le cas réel de la pièce 1052 : 40 x 2752,2936 = 110 091,744.
        var rapport = Controler(Ligne(40m, 2752.2936m, 110091.744m));

        var constat = Assert.Single(rapport.Constats, c => c.Code == "ARRONDI_NON_TRANCHE");
        Assert.Equal(Severite.Avertissement, constat.Severite);
        Assert.Contains("110091.744", constat.Message);
        Assert.Contains("110091.60", constat.Message);   // ce que donnerait un arrondi à 2 décimales
    }

    [Fact]
    public void Un_produit_entier_ne_declenche_rien()
    {
        var rapport = Controler(Ligne(20m, 10000m, 200000m));

        Assert.DoesNotContain(rapport.Constats, c => c.Code == "ARRONDI_NON_TRANCHE");
    }

    [Fact]
    public void Un_prix_a_decimales_qui_tombe_juste_ne_declenche_rien()
    {
        // 196,39 x 2500 = 490 975 : des décimales au prix, mais un total entier.
        var rapport = Controler(Ligne(196.39m, 2500m, 490975m));

        Assert.DoesNotContain(rapport.Constats, c => c.Code == "ARRONDI_NON_TRANCHE");
    }

    [Fact]
    public void Le_constat_ne_bloque_pas_la_piece()
    {
        // C'est un point à confirmer avec la DGI, pas une erreur de notre fait.
        Assert.False(Controler(Ligne(40m, 2752.2936m, 110091.744m)).ContientDesErreurs);
    }
}
