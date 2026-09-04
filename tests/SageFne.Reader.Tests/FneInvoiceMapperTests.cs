using Microsoft.Extensions.Options;
using SageFne.Core.Configuration;
using SageFne.Core.Mapping;
using SageFne.Core.Models.Fne;
using SageFne.Core.Models.Sage;
using SageFne.Core.Validation;

namespace SageFne.Core.Tests;

/// <summary>
/// Le mapping des taxes est le point où une erreur coûte le plus cher : une
/// TVA fausse est certifiée par la DGI et ne se rattrape plus. Ces tests
/// fixent la règle sur laquelle tout le reste s'appuie.
/// </summary>
public class FneInvoiceMapperTests
{
    // Sur une vente, « taxes » et « customTaxes » sont toujours présents —
    // vides au besoin, jamais absents. L'omission est réservée au bordereau
    // d'achat, dont le tableau de paramètres ne les comporte pas. Passer par
    // ces deux aides fait échouer le test si une vente perdait ses listes,
    // là où un « ! » l'aurait tue.
    private static IReadOnlyList<string> Taxes(FneInvoiceItem article)
    {
        Assert.NotNull(article.Taxes);
        return article.Taxes;
    }

    private static IReadOnlyList<FneCustomTax> Prelevements(FneInvoiceItem article)
    {
        Assert.NotNull(article.CustomTaxes);
        return article.CustomTaxes;
    }

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
        Identifiant = "1432262S",
        Pays = "COTE D'IVOIRE",
    };

    private static FneInvoiceMapper Mappeur(string regime = "Unknown") =>
        new(Options.Create(new FneOptions
        {
            PointOfSale = "SIEGE",
            Establishment = "PRINCIPAL",
            PaymentMethod = "deferred",
            Template = "B2B",
            ZeroVat = new() { Default = regime },
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

        Assert.Equal(["TVA"], Taxes(article));
        Assert.Empty(Prelevements(article));
    }

    [Fact]
    public void Tva_a_9_pourcent_donne_le_code_TVAB()
    {
        // Le dossier code ce taux « TVA » comme le taux normal : seul le taux
        // porté par la ligne permet de les distinguer.
        var article = Premier(Ligne(taxe1: 9m, code1: "TVA"));

        Assert.Equal(["TVAB"], Taxes(article));
    }

    [Fact]
    public void Airsi_part_en_customTaxes_et_pas_en_taxes()
    {
        var article = Premier(Ligne(taxe2: 1.5m, code2: "AIRSI"));

        // La TVA est à 0 % et son régime n'est pas classé : aucun code fiscal.
        // L'AIRSI, lui, est un prélèvement et part quand même en customTaxes.
        Assert.Empty(Taxes(article));
        var prelevement = Assert.Single(Prelevements(article));
        Assert.Equal("AIRSI", prelevement.Name);
        Assert.Equal(1.5m, prelevement.Amount);
    }

    [Fact]
    public void Une_tva_a_zero_sans_regime_ne_porte_aucun_code()
    {
        // TVAC et TVAD valent tous deux 0 % : le taux ne permet pas de choisir.
        // Deviner reviendrait à déclarer à la DGI un régime fiscal qu'on ignore.
        var article = Premier(Ligne());

        Assert.Empty(Taxes(article));
        Assert.Empty(Prelevements(article));
    }

    [Fact]
    public void Une_tva_a_zero_declaree_sans_regime_bloque_la_piece()
    {
        var rapport = new CheckReport();
        Mappeur().Map(Entete, [Ligne(code1: "TVA", taxe1: 0m)], Client, rapport);

        var constat = Assert.Single(rapport.Constats, c => c.Code == "ZERO_VAT_CATEGORY_UNKNOWN");
        Assert.Equal(Severite.Erreur, constat.Severite);
        Assert.True(rapport.ContientDesErreurs);
        Assert.Contains("TVAC", constat.Message);
        Assert.Contains("TVAD", constat.Message);
    }

    [Fact]
    public void Une_ligne_sans_TVA_renseignee_bloque_sous_un_autre_constat()
    {
        // Le blocage est le même — c'est ce qu'on veut — mais le constat oriente
        // vers Sage et non vers l'écriture d'une règle d'exonération.
        var rapport = new CheckReport();
        Mappeur().Map(Entete, [Ligne()], Client, rapport);

        var constat = Assert.Single(rapport.Constats, c => c.Code == "TVA_ABSENTE");
        Assert.Equal(Severite.Erreur, constat.Severite);
        Assert.True(rapport.ContientDesErreurs);
        Assert.DoesNotContain(rapport.Constats, c => c.Code == "ZERO_VAT_CATEGORY_UNKNOWN");
    }

    [Fact]
    public void Le_regime_conventionnel_donne_TVAC()
    {
        var facture = Mappeur(regime: "ConventionalExemption").Map(Entete, [Ligne()], Client);

        Assert.Equal(["TVAC"], Taxes(facture.Items.Single()));
    }

    [Fact]
    public void Le_regime_legal_donne_TVAD()
    {
        var facture = Mappeur(regime: "LegalExemptionTEE_RME").Map(Entete, [Ligne()], Client);

        Assert.Equal(["TVAD"], Taxes(facture.Items.Single()));
    }

    [Fact]
    public void Un_regime_classe_ne_bloque_plus_la_piece()
    {
        var rapport = new CheckReport();
        Mappeur(regime: "LegalExemptionTEE_RME").Map(Entete, [Ligne()], Client, rapport);

        Assert.DoesNotContain(rapport.Constats, c => c.Code == "ZERO_VAT_CATEGORY_UNKNOWN");
    }

    [Fact]
    public void Un_taux_inconnu_n_est_pas_traite_comme_une_exoneration()
    {
        // 12 % n'existe pas dans la nomenclature : la ligne n'est pas exonérée
        // pour autant, et il serait faux de la certifier en TVAD.
        var article = Premier(Ligne(taxe1: 12m, code1: "TVA"));

        Assert.Empty(Taxes(article));
    }

    [Fact]
    public void Tva_18_et_airsi_se_rangent_chacune_de_son_cote()
    {
        var article = Premier(Ligne(taxe1: 18m, code1: "TVA", taxe2: 1.5m, code2: "AIRSI"));

        Assert.Equal(["TVA"], Taxes(article));
        Assert.Equal(1.5m, Assert.Single(Prelevements(article)).Amount);
    }

    [Fact]
    public void Tva_9_et_airsi_se_rangent_chacune_de_son_cote()
    {
        var article = Premier(Ligne(taxe1: 9m, code1: "TVA", taxe2: 1.5m, code2: "AIRSI"));

        Assert.Equal(["TVAB"], Taxes(article));
        Assert.Equal("AIRSI", Assert.Single(Prelevements(article)).Name);
    }

    [Fact]
    public void Airsi_est_reconnue_quel_que_soit_son_emplacement()
    {
        // Rien ne garantit que le dossier gardera l'AIRSI en position 2.
        var enPremier = Premier(Ligne(taxe1: 1.5m, code1: "AIRSI", taxe2: 18m, code2: "TVA"));
        var enTroisieme = Premier(Ligne(taxe1: 18m, code1: "TVA", taxe3: 1.5m, code3: "airsi"));


        Assert.Equal(["TVA"], Taxes(enPremier));
        Assert.Equal("AIRSI", Assert.Single(Prelevements(enPremier)).Name);
        Assert.Equal(["TVA"], Taxes(enTroisieme));
        Assert.Equal("AIRSI", Assert.Single(Prelevements(enTroisieme)).Name);
    }

    [Fact]
    public void Un_taux_hors_nomenclature_est_signale_et_non_repris()
    {
        var rapport = new CheckReport();
        Mappeur().Map(Entete, [Ligne(taxe1: 12m, code1: "TVA")], Client, rapport);

        Assert.Contains(rapport.Constats, constat => constat.Code == "TAUX_HORS_NOMENCLATURE");
        Assert.Contains(rapport.Constats, constat => constat.Code == "LIGNE_SANS_CODE_TAXE");
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
        Assert.Equal("1432262S", facture.ClientNcc);
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
