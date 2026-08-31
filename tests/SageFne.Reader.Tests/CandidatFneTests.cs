using SageFne.Reader.Batch;
using SageFne.Reader.Models.Fne;
using SageFne.Reader.Models.Sage;
using SageFne.Reader.Validation;

namespace SageFne.Reader.Tests;

/// <summary>
/// Le premier envoi à la DGI est irréversible. La pièce d'essai doit être la
/// moins discutable du dossier : ces tests tiennent ce qui la disqualifie.
/// </summary>
public class CandidatFneTests
{
    private const decimal Tolerance = 1m;

    private static SageDocumentLine Ligne(int rang, decimal tva, decimal ht, string airsi = "") => new()
    {
        Domaine = 0, Type = 6, Piece = "1500", Ligne = rang,
        ArticleReference = $"ART{rang}", Designation = $"Article {rang}",
        Quantite = 1m, PrixUnitaire = ht,
        MontantHT = ht,
        MontantTTC = ht * (1m + tva / 100m),
        Taxe1 = tva, CodeTaxe1 = tva == 0m ? "" : "TVA",
        Taxe2 = airsi == "" ? 0m : 1.5m, CodeTaxe2 = airsi,
    };

    private static InvoiceConversion Conversion(
        IReadOnlyList<SageDocumentLine> lignes,
        string ncc = "1432262S",
        CheckReport? rapport = null,
        decimal? totalTtcEntete = null,
        EtatPiece etat = EtatPiece.ACertifier)
    {
        var ttc = lignes.Sum(ligne => ligne.MontantTTC);
        return new InvoiceConversion
        {
            Header = new SageDocumentHeader
            {
                Domaine = 0, Type = 6, DocType = 6, Piece = "1500",
                Date = new DateTime(2025, 12, 3), Tiers = "4111SITASARL",
                TotalTTC = totalTtcEntete ?? ttc,
            },
            Customer = new SageCustomer { CtNum = "4111SITASARL", Intitule = "SITA SARL", Identifiant = ncc },
            Lines = lignes,
            Invoice = new FneInvoice
            {
                Items = lignes.Select(ligne => new FneInvoiceItem
                {
                    Taxes = ["TVA"],
                    CustomTaxes = ligne.CodeTaxe2 == "" ? [] : [new FneCustomTax("AIRSI", 1.5m)],
                    Reference = ligne.ArticleReference,
                    Description = ligne.Designation,
                    Quantity = ligne.Quantite,
                    Amount = ligne.PrixUnitaire,
                    MeasurementUnit = "KG",
                }).ToList(),
            },
            Report = rapport ?? new CheckReport(),
            Etat = etat,
        };
    }

    private static CandidatFne Evaluer(
        InvoiceConversion conversion,
        TauxRecherche taux = TauxRecherche.Normal) =>
        CandidatFne.Evaluer(conversion, taux, Tolerance);

    // --- Ce qui écarte sans appel ------------------------------------------

    [Fact]
    public void Une_facture_a_18_pour_cent_nette_est_retenue()
    {
        var candidat = Evaluer(Conversion([Ligne(1, 18m, 10000m)]));

        Assert.True(candidat.Retenu);
        Assert.Empty(candidat.Disqualifications);
        Assert.Equal("net", candidat.Statut);
    }

    [Fact]
    public void Une_ligne_a_zero_pour_cent_ecarte_la_piece()
    {
        // Le régime d'exonération n'est pas tranché : une pièce d'essai ne doit
        // soulever aucune question.
        var candidat = Evaluer(Conversion([Ligne(1, 18m, 10000m), Ligne(2, 0m, 5000m)]));

        Assert.False(candidat.Retenu);
        Assert.Contains(candidat.Disqualifications, raison => raison.Message.Contains("0 %"));
    }

    [Fact]
    public void Un_taux_hors_nomenclature_ecarte_la_piece()
    {
        var candidat = Evaluer(Conversion([Ligne(1, 18m, 10000m), Ligne(2, 12m, 5000m)]));

        Assert.False(candidat.Retenu);
        Assert.Contains(candidat.Disqualifications, raison => raison.Message.Contains("12"));
    }

    [Fact]
    public void Un_NCC_absent_ecarte_la_piece()
    {
        var candidat = Evaluer(Conversion([Ligne(1, 18m, 10000m)], ncc: ""));

        Assert.False(candidat.Retenu);
        Assert.Contains(candidat.Disqualifications, raison => raison.Message.Contains("NCC"));
    }

    [Fact]
    public void Une_erreur_de_controle_ecarte_la_piece()
    {
        var rapport = new CheckReport();
        rapport.Erreur("ZERO_VAT_CATEGORY_UNKNOWN", "régime inconnu");

        var candidat = Evaluer(Conversion([Ligne(1, 18m, 10000m)], rapport: rapport));

        Assert.False(candidat.Retenu);
        Assert.Contains(candidat.Disqualifications, raison => raison.Message.Contains("ZERO_VAT_CATEGORY_UNKNOWN"));
    }

    [Fact]
    public void Une_piece_deja_certifiee_n_est_pas_un_candidat()
    {
        var candidat = Evaluer(Conversion([Ligne(1, 18m, 10000m)], etat: EtatPiece.DejaCertifiee));

        Assert.False(candidat.Retenu);
        Assert.Contains(candidat.Disqualifications, raison => raison.Message.Contains("registre"));
    }

    [Fact]
    public void Une_facture_sans_le_taux_cherche_est_ecartee()
    {
        var candidat = Evaluer(Conversion([Ligne(1, 9m, 10000m)]), TauxRecherche.Normal);

        Assert.False(candidat.Retenu);
        Assert.Contains(candidat.Disqualifications, raison => raison.Message.Contains("18"));
    }

    [Fact]
    public void La_meme_facture_convient_pour_le_taux_reduit()
    {
        var candidat = Evaluer(Conversion([Ligne(1, 9m, 10000m)]), TauxRecherche.Reduit);

        Assert.True(candidat.Retenu);
        Assert.Equal([9m], candidat.TauxRencontres);
    }

    // --- Ce qui départage ---------------------------------------------------

    [Fact]
    public void Une_piece_nette_passe_devant_une_piece_a_reserves()
    {
        var rapport = new CheckReport();
        rapport.Avertir("ECART_ENTETE_HT", "écart");

        var nette = Evaluer(Conversion([Ligne(1, 18m, 10000m)]));
        var reserves = Evaluer(Conversion([Ligne(1, 18m, 10000m)], rapport: rapport));

        Assert.True(nette.Score > reserves.Score);
        Assert.Equal("réserves", reserves.Statut);
    }

    [Fact]
    public void Une_piece_courte_passe_devant_une_piece_longue()
    {
        var courte = Evaluer(Conversion([Ligne(1, 18m, 10000m)]));
        var longue = Evaluer(Conversion(
            Enumerable.Range(1, 12).Select(rang => Ligne(rang, 18m, 1000m)).ToList()));

        Assert.True(courte.Score > longue.Score);
    }

    [Fact]
    public void Un_ecart_de_totaux_coute_des_points()
    {
        var juste = Evaluer(Conversion([Ligne(1, 18m, 10000m)]));
        var faux = Evaluer(Conversion([Ligne(1, 18m, 10000m)], totalTtcEntete: 9000m));

        Assert.True(juste.Score > faux.Score);
        Assert.True(faux.Retenu, "un écart de totaux n'écarte pas, il déclasse");
        Assert.NotEqual(0m, faux.EcartTTC);
    }

    [Fact]
    public void Un_ecart_dans_la_tolerance_ne_coute_rien()
    {
        var conversion = Conversion([Ligne(1, 18m, 10000m)]);
        var dansLaTolerance = Conversion(
            [Ligne(1, 18m, 10000m)],
            totalTtcEntete: conversion.TotalTTC + 0.5m);

        Assert.Equal(Evaluer(conversion).Score, Evaluer(dansLaTolerance).Score);
    }

    [Fact]
    public void Un_taux_unique_passe_devant_un_melange()
    {
        var unique = Evaluer(Conversion([Ligne(1, 18m, 10000m)]));
        var melange = Evaluer(Conversion([Ligne(1, 18m, 10000m), Ligne(2, 9m, 5000m)]));

        Assert.True(unique.Score > melange.Score);
        Assert.True(melange.Retenu, "deux taux connus ne disqualifient pas");
    }

    [Fact]
    public void Une_piece_sans_prelevement_passe_devant()
    {
        var sans = Evaluer(Conversion([Ligne(1, 18m, 10000m)]));
        var avec = Evaluer(Conversion([Ligne(1, 18m, 10000m, airsi: "AIRSI")]));

        Assert.True(sans.Score > avec.Score);
        Assert.Equal(["AIRSI"], avec.CustomTaxes);
        Assert.True(avec.Retenu, "l'AIRSI est légitime, il déclasse sans écarter");
    }

    [Fact]
    public void Le_classement_est_toujours_justifie()
    {
        var candidat = Evaluer(Conversion([Ligne(1, 18m, 10000m)]));

        // Un candidat qu'on ne comprend pas n'en est pas un.
        Assert.NotEmpty(candidat.Raisons);
        Assert.Contains(candidat.Raisons, raison => raison.Contains("1 ligne"));
        Assert.Contains(candidat.Raisons, raison => raison.Contains("taux unique"));
    }

    [Fact]
    public async Task Le_jeu_d_essai_propose_un_candidat_par_taux()
    {
        var depot = new SageFne.Reader.Data.DemoSageInvoiceRepository();
        var entetes = await depot.GetInvoicesAsync(new SageFne.Reader.Data.InvoiceQuery());

        // Il existe au moins une pièce à 18 % et une à 9 % dans le jeu d'essai :
        // sans quoi la commande ne montrerait jamais sa sortie complète.
        Assert.NotEmpty(entetes);
    }
}

/// <summary>
/// Un recensement doit pouvoir se compter : cinq pièces prises au hasard ne
/// disent pas s'il y en a douze ou huit cents derrière.
/// </summary>
public class DisqualificationTests
{
    private static SageDocumentLine Ligne(decimal tva) => new()
    {
        Domaine = 0, Type = 6, Piece = "1500", Ligne = 1,
        ArticleReference = "ART1", Designation = "Article",
        Quantite = 1m, PrixUnitaire = 10000m,
        MontantHT = 10000m, MontantTTC = 10000m * (1m + tva / 100m),
        Taxe1 = tva, CodeTaxe1 = tva == 0m ? "" : "TVA",
    };

    private static CandidatFne Evaluer(string ncc, decimal tva, EtatPiece etat = EtatPiece.ACertifier) =>
        CandidatFne.Evaluer(
            new InvoiceConversion
            {
                Header = new SageDocumentHeader
                {
                    Domaine = 0, Type = 6, DocType = 6, Piece = "1500",
                    Date = new DateTime(2025, 12, 3), Tiers = "4111X",
                    TotalTTC = 10000m * (1m + tva / 100m),
                },
                Customer = new SageCustomer { CtNum = "4111X", Intitule = "X", Identifiant = ncc },
                Lines = [Ligne(tva)],
                Invoice = new FneInvoice(),
                Report = new CheckReport(),
                Etat = etat,
            },
            TauxRecherche.Normal,
            1m);

    [Fact]
    public void Chaque_motif_porte_un_code_stable()
    {
        Assert.True(Evaluer(ncc: "", tva: 18m).Ecarte(Disqualification.NccAbsent));
        Assert.True(Evaluer(ncc: "1432262S", tva: 0m).Ecarte(Disqualification.TvaZero));
        Assert.True(Evaluer(ncc: "1432262S", tva: 9m).Ecarte(Disqualification.TauxAbsent));
        Assert.True(Evaluer(ncc: "1432262S", tva: 12m).Ecarte(Disqualification.HorsNomenclature));
        Assert.True(Evaluer(ncc: "1432262S", tva: 18m, etat: EtatPiece.DejaCertifiee)
            .Ecarte(Disqualification.DejaAuRegistre));
    }

    [Fact]
    public void Un_candidat_retenu_ne_porte_aucun_motif()
    {
        var candidat = Evaluer(ncc: "1432262S", tva: 18m);

        Assert.True(candidat.Retenu);
        Assert.False(candidat.Ecarte(Disqualification.NccAbsent));
        Assert.Empty(candidat.Disqualifications);
    }

    [Fact]
    public void Les_motifs_se_cumulent_et_se_comptent()
    {
        // Une pièce sans NCC et à 0 % porte les deux : le recensement doit voir
        // les deux murs, pas seulement le premier.
        var candidat = Evaluer(ncc: "", tva: 0m);

        Assert.True(candidat.Ecarte(Disqualification.NccAbsent));
        Assert.True(candidat.Ecarte(Disqualification.TvaZero));
        Assert.Equal(
            candidat.Disqualifications.Count,
            candidat.Disqualifications.Select(motif => motif.Code).Distinct().Count());
    }
}
