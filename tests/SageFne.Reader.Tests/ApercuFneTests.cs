using Microsoft.Extensions.Options;
using SageFne.Reader.Configuration;
using SageFne.Reader.Mapping;
using SageFne.Reader.Models.Sage;
using SageFne.Reader.Validation;

namespace SageFne.Reader.Tests;

/// <summary>
/// Ce que Sage ne porte pas ne s'invente pas. Ces tests tiennent la frontière
/// entre ce qui vient du dossier et ce qui vient du paramétrage.
/// </summary>
public class ApercuFneTests
{
    private static FneOptions Reglages(
        string pointDeVente = "FISH-AFRIC",
        string etablissement = "FISH-AFRIC") => new()
    {
        PointOfSale = pointDeVente,
        Establishment = etablissement,
        Template = "B2B",
        PaymentMethod = "deferred",
        ZeroVat = new() { Default = "LegalExemptionTEE_RME" },
    };

    private static readonly SageDocumentHeader Entete = new()
    {
        Domaine = 0, Type = 7, DocType = 6, Piece = "1052",
        Date = new DateTime(2025, 10, 22), Tiers = "4111GEMSCI",
        TotalTTC = 120000m,
    };

    private static SageCustomer Client(string email = "contact@gems.ci", string tel = "0700000000") => new()
    {
        CtNum = "4111GEMSCI",
        Intitule = "GEMS-CI",
        Identifiant = "1010983N",
        Email = email,
        Telephone = tel,
    };

    private static SageDocumentLine Ligne() => new()
    {
        Domaine = 0, Type = 7, Piece = "1052", Ligne = 1000,
        ArticleReference = "ART1", Designation = "Article",
        Quantite = 1m, PrixUnitaire = 110091.744m,
        MontantHT = 110091.744m, MontantTTC = 120000m,
        Unite = "KG",
        Taxe1 = 9m, CodeTaxe1 = "TVA",
    };

    // --- Les identifiants du dossier ---------------------------------------

    [Fact]
    public void Le_point_de_vente_et_l_etablissement_arrivent_dans_le_payload()
    {
        // Les secrets « Fne:PointOfSale » et « Fne:Establishment » se lient sur
        // FneOptions : c'est bien cette valeur qui part.
        var facture = new FneInvoiceMapper(Options.Create(Reglages()))
            .Map(Entete, [Ligne()], Client());

        Assert.Equal("FISH-AFRIC", facture.PointOfSale);
        Assert.Equal("FISH-AFRIC", facture.Establishment);
    }

    [Fact]
    public void Un_point_de_vente_vide_est_un_champ_obligatoire_manquant()
    {
        var facture = new FneInvoiceMapper(Options.Create(Reglages(pointDeVente: "")))
            .Map(Entete, [Ligne()], Client());

        var manques = FneCompleteness.Verifier(facture, "B2B");

        Assert.Contains(manques, manque => manque.Champ == "pointOfSale");
    }

    [Fact]
    public void Un_gabarit_non_remplace_compte_comme_manquant()
    {
        // « A_COMPLETER » partirait tel quel et serait certifié tel quel.
        var facture = new FneInvoiceMapper(Options.Create(Reglages(etablissement: "A_COMPLETER")))
            .Map(Entete, [Ligne()], Client());

        Assert.Contains(
            FneCompleteness.Verifier(facture, "B2B"),
            manque => manque.Champ == "establishment");
    }

    [Fact]
    public void Des_identifiants_renseignes_ne_manquent_pas()
    {
        var facture = new FneInvoiceMapper(Options.Create(Reglages()))
            .Map(Entete, [Ligne()], Client());

        var manques = FneCompleteness.Verifier(facture, "B2B");

        Assert.DoesNotContain(manques, manque => manque.Champ is "pointOfSale" or "establishment");
        Assert.Empty(manques);
    }

    // --- Ce qui n'est pas dans Sage se signale ------------------------------

    [Fact]
    public void Un_client_sans_courriel_est_signale_et_rien_n_est_invente()
    {
        var rapport = new CheckReport();
        InvoiceValidator.Validate(Entete, Client(email: ""), [Ligne()], "B2B", rapport);

        var constat = Assert.Single(rapport.Constats, c => c.Code == "CLIENT_SANS_EMAIL");
        Assert.Equal(Severite.Avertissement, constat.Severite);
        Assert.Contains("Aucune adresse n'est inventée", constat.Message);
    }

    [Fact]
    public void Un_client_sans_telephone_est_signale_aussi()
    {
        var rapport = new CheckReport();
        InvoiceValidator.Validate(Entete, Client(tel: ""), [Ligne()], "B2B", rapport);

        Assert.Single(rapport.Constats, c => c.Code == "CLIENT_SANS_TELEPHONE");
    }

    [Fact]
    public void Un_courriel_vide_part_vide_et_non_rempli()
    {
        var facture = new FneInvoiceMapper(Options.Create(Reglages()))
            .Map(Entete, [Ligne()], Client(email: ""));

        Assert.Equal("", facture.ClientEmail);
    }

    [Fact]
    public void Le_mode_de_reglement_se_declare_comme_suppose_sur_chaque_piece()
    {
        // Sage ne porte pas le mode de règlement : la valeur vient du
        // paramétrage, et cela doit se voir sur la pièce, pas en note de bas
        // de page.
        var rapport = new CheckReport();
        new FneInvoiceMapper(Options.Create(Reglages())).Map(Entete, [Ligne()], Client(), rapport);

        var constat = Assert.Single(rapport.Constats, c => c.Code == "PAYMENT_METHOD_SUPPOSE");
        Assert.Equal(Severite.Avertissement, constat.Severite);
        Assert.Contains("deferred", constat.Message);
        Assert.Contains("Sage ne porte pas", constat.Message);
    }

    [Fact]
    public void Les_valeurs_figees_sont_annoncees_comme_telles()
    {
        var facture = new FneInvoiceMapper(Options.Create(Reglages()))
            .Map(Entete, [Ligne()], Client());

        var hypotheses = FneCompleteness.Hypotheses(facture).Select(h => h.Champ).ToList();

        Assert.Contains("paymentMethod", hypotheses);
        Assert.Contains("invoiceType", hypotheses);
        Assert.Contains("clientSellerName", hypotheses);
    }

    // --- Ce qui vient bien de Sage ------------------------------------------

    [Fact]
    public void Les_champs_du_client_viennent_de_F_COMPTET()
    {
        var facture = new FneInvoiceMapper(Options.Create(Reglages()))
            .Map(Entete, [Ligne()], Client());

        Assert.Equal("1010983N", facture.ClientNcc);
        Assert.Equal("GEMS-CI", facture.ClientCompanyName);
        Assert.Equal("contact@gems.ci", facture.ClientEmail);
        Assert.Equal("0700000000", facture.ClientPhone);
    }

    [Fact]
    public void La_ligne_porte_le_prix_unitaire_et_non_le_montant()
    {
        var facture = new FneInvoiceMapper(Options.Create(Reglages()))
            .Map(Entete, [Ligne()], Client());

        var item = Assert.Single(facture.Items);
        Assert.Equal(110091.744m, item.Amount);
        Assert.Equal(1m, item.Quantity);
        Assert.Equal("KG", item.MeasurementUnit);
        Assert.Equal(["TVAB"], item.Taxes);
    }

    [Fact]
    public void Rien_n_est_ajoute_au_dela_de_ce_que_Sage_porte()
    {
        // clientSellerName, commercialMessage et footer restent vides : aucune
        // source dans le dossier.
        var facture = new FneInvoiceMapper(Options.Create(Reglages()))
            .Map(Entete, [Ligne()], Client());

        Assert.Equal("", facture.ClientSellerName);
        Assert.Equal("", facture.CommercialMessage);
        Assert.Equal("", facture.Footer);
        Assert.False(facture.IsRne);
        Assert.Equal("sale", facture.InvoiceType);
    }
}

/// <summary>
/// « isRne » décrit le régime fiscal de l'entreprise émettrice — le vôtre, pas
/// celui du client. Il part sur chaque facture certifiée.
/// </summary>
public class RegimeEmetteurTests
{
    private static FneOptions Reglages(bool rne) => new()
    {
        PointOfSale = "FISH-AFRIC",
        Establishment = "FISH-AFRIC",
        Template = "B2B",
        PaymentMethod = "deferred",
        IsRne = rne,
        ZeroVat = new() { Default = "LegalExemptionTEE_RME" },
    };

    private static readonly SageDocumentHeader Entete = new()
    {
        Domaine = 0, Type = 7, DocType = 6, Piece = "1052",
        Date = new DateTime(2025, 10, 22), Tiers = "4111GEMSCI", TotalTTC = 120000m,
    };

    private static readonly SageCustomer Client = new()
    {
        CtNum = "4111GEMSCI", Intitule = "GEMS-CI", Identifiant = "1010983N",
    };

    private static SageDocumentLine Ligne() => new()
    {
        Domaine = 0, Type = 7, Piece = "1052", Ligne = 1000,
        ArticleReference = "P007", Designation = "POITRINE DE POULET",
        Quantite = 40m, PrixUnitaire = 2752.2936m,
        MontantHT = 110091.744m, MontantTTC = 120000m,
        Unite = "PKT", Taxe1 = 9m, CodeTaxe1 = "TVA",
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Le_regime_declare_est_celui_qui_part(bool rne)
    {
        var facture = new FneInvoiceMapper(Options.Create(Reglages(rne)))
            .Map(Entete, [Ligne()], Client);

        Assert.Equal(rne, facture.IsRne);
    }

    [Fact]
    public void Le_defaut_reste_false()
    {
        Assert.False(new FneOptions().IsRne);
    }

    [Fact]
    public void Le_regime_est_annonce_comme_une_declaration_a_relire()
    {
        // Ce n'est pas une valeur devinée depuis Sage : elle doit se voir.
        var facture = new FneInvoiceMapper(Options.Create(Reglages(false)))
            .Map(Entete, [Ligne()], Client);

        var hypothese = Assert.Single(FneCompleteness.Hypotheses(facture), h => h.Champ == "isRne");
        Assert.Contains("le vôtre, pas celui du client", hypothese.Consequence);
    }
}
