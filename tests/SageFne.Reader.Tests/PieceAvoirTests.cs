using SageFne.Core.Models.Sage;
using SageFne.Core.Validation;

namespace SageFne.Core.Tests;

/// <summary>
/// Une pièce qui corrige une facture, plutôt qu'une facture.
/// </summary>
/// <remarks>
/// Vécu sur le dossier réel : la pièce 205, −8 720 F, bloquée sur
/// « QUANTITE_INVALIDE ». Le blocage était juste — la DGI n'accepte pas de
/// vente négative — mais le motif faisait chercher une erreur de saisie qui
/// n'existait pas. La pièce était parfaitement correcte dans Sage.
///
/// Ce qui est éprouvé ici : qu'un avoir se reconnaisse et se dise en un seul
/// constat, et qu'une facture ordinaire avec une ligne fautive continue d'être
/// signalée comme telle.
/// </remarks>
public class PieceAvoirTests
{
    private static SageDocumentLine Ligne(
        decimal quantite, decimal montantHT, string reference = "", int rang = 1000) => new()
    {
        Domaine = 0, Type = 6, Piece = "205", Ligne = rang,
        ArticleReference = "ART1", Designation = "Article",
        Quantite = quantite, PrixUnitaire = 1000m,
        MontantHT = montantHT, MontantTTC = montantHT * 1.18m,
        Unite = "PCE", Taxe1 = 18m, CodeTaxe1 = "TVA",
        DocumentReference = reference,
    };

    // --- Ce qui est un avoir -------------------------------------------------

    [Fact]
    public void Des_lignes_toutes_negatives_font_un_avoir() =>
        Assert.True(PieceAvoir.Est([Ligne(-2m, -2000m), Ligne(-1m, -1000m, rang: 2000)]));

    [Fact]
    public void Un_total_negatif_fait_un_avoir_meme_avec_une_quantite_positive()
    {
        // Vu aussi : la quantité reste positive et c'est le montant qui porte
        // le signe. Le total tranche.
        Assert.True(PieceAvoir.Est([Ligne(1m, -8720m)]));
    }

    // --- Ce qui n'en est pas -------------------------------------------------

    [Fact]
    public void Une_facture_ordinaire_n_est_pas_un_avoir() =>
        Assert.False(PieceAvoir.Est([Ligne(2m, 2000m), Ligne(1m, 1000m, rang: 2000)]));

    [Fact]
    public void Une_piece_mixte_a_total_positif_n_est_pas_un_avoir()
    {
        // Des lignes positives, une ligne négative, un total positif : c'est
        // une facture ordinaire dont une ligne pose problème. La confondre avec
        // un avoir masquerait une vraie erreur de saisie.
        Assert.False(PieceAvoir.Est([Ligne(10m, 10000m), Ligne(-1m, -1000m, rang: 2000)]));
    }

    [Fact]
    public void Une_piece_sans_ligne_n_est_pas_un_avoir() => Assert.False(PieceAvoir.Est([]));

    // --- La référence portée -------------------------------------------------

    [Fact]
    public void La_reference_du_document_est_rendue_quand_les_lignes_s_accordent() =>
        Assert.Equal("1234", PieceAvoir.ReferencePortee(
            [Ligne(-1m, -1000m, "1234"), Ligne(-1m, -1000m, "1234", rang: 2000)]));

    [Fact]
    public void Des_references_discordantes_ne_designent_rien()
    {
        // DO_Ref est un champ libre. Deux valeurs différentes ne désignent
        // aucune facture, et en choisir une serait deviner.
        Assert.Null(PieceAvoir.ReferencePortee(
            [Ligne(-1m, -1000m, "1234"), Ligne(-1m, -1000m, "5678", rang: 2000)]));
    }

    [Fact]
    public void Une_reference_absente_ne_s_invente_pas() =>
        Assert.Null(PieceAvoir.ReferencePortee([Ligne(-1m, -1000m)]));

    // --- Ce que le rapport dit ----------------------------------------------

    private static CheckReport Valider(params SageDocumentLine[] lignes)
    {
        var rapport = new CheckReport();
        InvoiceValidator.Validate(
            new SageDocumentHeader
            {
                Domaine = 0, Type = 6, DocType = 6, Piece = "205",
                Date = new DateTime(2026, 9, 4), Tiers = "4111SOCO",
            },
            new SageCustomer
            {
                CtNum = "4111SOCO", Intitule = "SOCOPRIX",
                Identifiant = "874585U", Telephone = "0700000000",
            },
            lignes, "B2B", rapport);

        return rapport;
    }

    [Fact]
    public void Un_avoir_porte_un_seul_constat_au_lieu_de_douze()
    {
        // Le défaut d'origine : trois lignes négatives donnaient trois
        // « QUANTITE_INVALIDE », et l'exploitant cherchait une faute de saisie.
        var rapport = Valider(
            Ligne(-2m, -2000m), Ligne(-3m, -3000m, rang: 2000), Ligne(-1m, -1000m, rang: 3000));

        Assert.Contains(rapport.Constats, c => c.Code == "PIECE_AVOIR");
        Assert.DoesNotContain(rapport.Constats, c => c.Code == "QUANTITE_INVALIDE");
    }

    [Fact]
    public void Le_constat_nomme_la_commande_et_la_facture_d_origine()
    {
        var constat = Assert.Single(
            Valider(Ligne(-2m, -2000m, "1234")).Constats.Where(c => c.Code == "PIECE_AVOIR"));

        Assert.Contains("avoir", constat.Message);
        Assert.Contains("ORIGINE", constat.Message);
        Assert.Contains("1234", constat.Message);
    }

    [Fact]
    public void Sans_reference_le_constat_ne_promet_rien()
    {
        var constat = Assert.Single(
            Valider(Ligne(-2m, -2000m)).Constats.Where(c => c.Code == "PIECE_AVOIR"));

        Assert.DoesNotContain("DO_Ref", constat.Message);
    }

    [Fact]
    public void Un_avoir_reste_bloque()
    {
        // Le motif change, la conséquence non : rien ne part en « sale »
        // négatif vers la DGI.
        Assert.True(Valider(Ligne(-2m, -2000m)).ContientDesErreurs);
    }

    [Fact]
    public void Une_quantite_nulle_sur_une_facture_ordinaire_reste_une_erreur()
    {
        // La règle d'origine n'est pas affaiblie : un zéro isolé au milieu de
        // lignes positives est une vraie anomalie de saisie.
        var rapport = Valider(Ligne(10m, 10000m), Ligne(0m, 0m, rang: 2000));

        Assert.Contains(rapport.Constats, c => c.Code == "QUANTITE_INVALIDE");
        Assert.DoesNotContain(rapport.Constats, c => c.Code == "PIECE_AVOIR");
    }
}
