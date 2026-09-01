using SageFne.Reader.Audit;
using SageFne.Reader.Models.Sage;

namespace SageFne.Reader.Tests;

/// <summary>
/// La campagne de saisie des NCC : ce qui manque, et par quel appel commencer.
/// </summary>
/// <remarks>
/// Le NCC vit dans Sage et s'y corrige. Cette analyse ne produit qu'une liste
/// d'appels : rien n'y écrit, et rien n'y devine un numéro. Ce qu'elle doit
/// faire juste, c'est l'ordre — un classement faux fait passer les mauvais
/// coups de téléphone en premier.
/// </remarks>
public class CampagneNccTests
{
    private static SageDocumentHeader Entete(
        string piece, string tiers, short type = 6, int jour = 15) => new()
    {
        Piece = piece,
        Domaine = 0,
        Type = type,
        DocType = 6,
        Date = new DateTime(2025, 10, jour),
        Tiers = tiers,
        // Volontairement à zéro : ce dossier en porte, et le montant doit
        // malgré tout se calculer sur les lignes.
        TotalTTC = 0m,
    };

    private static SageDocumentLine Ligne(string piece, decimal montantTTC) => new()
    {
        Piece = piece,
        Domaine = 0,
        Type = 6,
        Ligne = 1,
        ArticleReference = "ART",
        Designation = "Article",
        Quantite = 1m,
        MontantHT = montantTTC / 1.18m,
        MontantTTC = montantTTC,
    };

    private static SageCustomer Client(
        string ctNum, string ncc = "", string intitule = "Client",
        string telephone = "", string email = "") => new()
    {
        CtNum = ctNum,
        Intitule = intitule,
        Identifiant = ncc,
        Telephone = telephone,
        Email = email,
    };

    private static EtatCampagneNcc Analyser(
        IEnumerable<SageDocumentHeader> entetes,
        IEnumerable<SageDocumentLine> lignes,
        params SageCustomer[] clients) =>
        CampagneNcc.Analyser(
            entetes.ToList(),
            lignes.ToList(),
            clients.ToDictionary(client => client.CtNum, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Un_client_sans_ncc_entre_dans_la_campagne()
    {
        var etat = Analyser(
            [Entete("1", "C1")],
            [Ligne("1", 1000m)],
            Client("C1"));

        Assert.Equal(1, etat.FacturesSansNcc);
        Assert.Equal(0, etat.FacturesCouvertes);
        Assert.Equal("C1", Assert.Single(etat.Comptes).CtNum);
    }

    [Fact]
    public void Un_client_avec_ncc_n_y_entre_pas()
    {
        var etat = Analyser(
            [Entete("1", "C1")],
            [Ligne("1", 1000m)],
            Client("C1", "1432262S"));

        Assert.Equal(0, etat.FacturesSansNcc);
        Assert.Equal(1, etat.FacturesCouvertes);
        Assert.Empty(etat.Comptes);
        Assert.Equal(1, etat.ComptesRenseignes);
    }

    [Theory]
    [InlineData("A_COMPLETER")]
    [InlineData("a_completer")]
    [InlineData("  TODO  ")]
    [InlineData("NEANT")]
    [InlineData("-")]
    [InlineData("   ")]
    public void Un_gabarit_non_remplace_compte_comme_absent(string valeur)
    {
        // « A_COMPLETER » partirait tel quel chez la DGI, et serait certifié
        // tel quel. Le traiter comme un NCC renseigné retirerait de la campagne
        // les factures qu'elle existe pour rattraper.
        var etat = Analyser(
            [Entete("1", "C1")],
            [Ligne("1", 1000m)],
            Client("C1", valeur));

        Assert.Equal(1, etat.FacturesSansNcc);
    }

    [Fact]
    public void Le_montant_se_calcule_sur_les_lignes_et_non_sur_l_entete()
    {
        // DO_TotalTTC vaut 0 sur une partie de ce dossier. S'y fier classerait
        // le plus gros compte en queue de liste, et la campagne commencerait
        // par les mauvais appels.
        var etat = Analyser(
            [Entete("1", "C1")],
            [Ligne("1", 750_000m)],
            Client("C1"));

        Assert.Equal(750_000m, Assert.Single(etat.Comptes).MontantTTC);
        Assert.Equal(750_000m, etat.MontantSansNcc);
    }

    [Fact]
    public void Les_comptes_sont_classes_par_montant_en_jeu()
    {
        // Trois petites factures pèsent moins qu'une grosse : c'est le montant
        // qui décide de l'ordre des appels, pas le nombre.
        var etat = Analyser(
            [Entete("1", "PETIT"), Entete("2", "PETIT"), Entete("3", "PETIT"), Entete("4", "GROS")],
            [Ligne("1", 1_000m), Ligne("2", 1_000m), Ligne("3", 1_000m), Ligne("4", 900_000m)],
            Client("PETIT"), Client("GROS"));

        Assert.Equal(["GROS", "PETIT"], etat.Comptes.Select(compte => compte.CtNum));
    }

    [Fact]
    public void Une_piece_comptabilisee_ne_compte_pas_deux_fois()
    {
        // DO_Type 6 et 7 sont la même facture : l'identité ne bouge pas à la
        // comptabilisation. La compter deux fois gonflerait la campagne
        // d'appels qui n'existent pas.
        var etat = Analyser(
            [Entete("1", "C1", type: 6), Entete("1", "C1", type: 7)],
            [Ligne("1", 1_000m)],
            Client("C1"));

        Assert.Equal(1, etat.Factures);
        Assert.Equal(1, etat.FacturesSansNcc);
        Assert.Equal(1, Assert.Single(etat.Comptes).Factures);
    }

    [Fact]
    public void Un_compte_facture_sans_fiche_se_signale_comme_tel()
    {
        // Ce n'est pas un NCC qui manque, c'est le client : on ne peut pas
        // appeler quelqu'un dont on n'a pas la fiche.
        var etat = Analyser(
            [Entete("1", "FANTOME")],
            [Ligne("1", 1_000m)]);

        var compte = Assert.Single(etat.Comptes);
        Assert.True(compte.FicheIntrouvable);
        Assert.Equal("", compte.Intitule);
    }

    [Fact]
    public void Les_dates_encadrent_les_factures_du_compte()
    {
        var etat = Analyser(
            [Entete("1", "C1", jour: 3), Entete("2", "C1", jour: 27)],
            [Ligne("1", 100m), Ligne("2", 100m)],
            Client("C1"));

        var compte = Assert.Single(etat.Comptes);
        Assert.Equal(3, compte.PremiereFacture.Day);
        Assert.Equal(27, compte.DerniereFacture.Day);
    }

    [Fact]
    public void Le_moyen_de_contact_dit_ce_dont_on_dispose()
    {
        var etat = Analyser(
            [Entete("1", "C1"), Entete("2", "C2"), Entete("3", "C3")],
            [Ligne("1", 300m), Ligne("2", 200m), Ligne("3", 100m)],
            Client("C1", telephone: "07 00 00 00 00"),
            Client("C2", email: "compta@exemple.ci"),
            Client("C3"));

        var parCompte = etat.Comptes.ToDictionary(compte => compte.CtNum);

        Assert.Equal("07 00 00 00 00", parCompte["C1"].MoyenDeContact);
        Assert.Equal("compta@exemple.ci", parCompte["C2"].MoyenDeContact);
        Assert.Equal("— aucun —", parCompte["C3"].MoyenDeContact);
    }

    [Fact]
    public void Le_nombre_d_appels_pour_couvrir_la_moitie_se_compte_en_factures()
    {
        // Un compte porte six factures, six comptes en portent une chacun.
        // La moitié de douze tient dans le premier appel ; les quatre
        // cinquièmes en demandent cinq. C'est ce chiffre qui décide de la
        // manière de mener la campagne — un seul appel, ou toute une tournée.
        var entetes = new List<SageDocumentHeader>();
        var lignes = new List<SageDocumentLine>();
        var clients = new List<SageCustomer> { Client("GROS") };

        for (var rang = 1; rang <= 6; rang++)
        {
            entetes.Add(Entete($"G{rang}", "GROS"));
            lignes.Add(Ligne($"G{rang}", 100m));
        }

        for (var rang = 1; rang <= 6; rang++)
        {
            entetes.Add(Entete($"P{rang}", $"PETIT{rang}"));
            lignes.Add(Ligne($"P{rang}", 100m));
            clients.Add(Client($"PETIT{rang}"));
        }

        var etat = Analyser(entetes, lignes, clients.ToArray());

        Assert.Equal(12, etat.FacturesSansNcc);
        Assert.Equal(1, etat.ComptesPour(0.5m));
        Assert.Equal(5, etat.ComptesPour(0.8m));
    }

    [Fact]
    public void Sans_manque_aucun_appel_n_est_a_passer()
    {
        var etat = Analyser(
            [Entete("1", "C1")],
            [Ligne("1", 1_000m)],
            Client("C1", "1432262S"));

        Assert.Equal(0, etat.ComptesPour(0.5m));
    }

    // --- Ce que le dossier porte déjà ---------------------------------------

    [Fact]
    public void Les_formes_observees_decrivent_sans_prescrire()
    {
        // La commande n'a aucune autorité pour dire à quoi ressemble un NCC
        // valide. Elle dit à quoi ressemblent ceux du dossier, et c'est tout.
        var etat = Analyser(
            [Entete("1", "C1"), Entete("2", "C2"), Entete("3", "C3")],
            [Ligne("1", 100m), Ligne("2", 100m), Ligne("3", 100m)],
            Client("C1", "1432262S"), Client("C2", "9988776C"), Client("C3", "CI-2026-0001"));

        var courante = etat.Formes[0];

        Assert.Equal("9999999A", courante.Gabarit);
        Assert.Equal(8, courante.Longueur);
        Assert.Equal(2, courante.Comptes);

        // La forme rare est présentée, pas écartée.
        Assert.Contains(etat.Formes, forme => forme.Gabarit == "AA-9999-9999");
    }

    [Fact]
    public void Un_meme_ncc_sur_deux_comptes_se_signale()
    {
        // Les factures des deux comptes partiraient sous un seul contribuable,
        // qui verrait apparaître chez lui des ventes qu'il n'a pas faites.
        var etat = Analyser(
            [Entete("1", "C1"), Entete("2", "C2"), Entete("3", "C2")],
            [Ligne("1", 100m), Ligne("2", 100m), Ligne("3", 100m)],
            Client("C1", "1432262S", "Société A"), Client("C2", "1432262S", "Société B"));

        var partage = Assert.Single(etat.Partages);

        Assert.Equal("1432262S", partage.Ncc);
        Assert.Equal(["C1", "C2"], partage.Comptes);
        Assert.Equal(3, partage.Factures);
    }

    [Fact]
    public void Un_ncc_porte_par_un_seul_compte_ne_se_signale_pas()
    {
        var etat = Analyser(
            [Entete("1", "C1"), Entete("2", "C1")],
            [Ligne("1", 100m), Ligne("2", 100m)],
            Client("C1", "1432262S"));

        Assert.Empty(etat.Partages);
    }

    [Theory]
    [InlineData("C1", "numéro de compte")]
    [InlineData("000000", "un seul caractère")]
    [InlineData("12", "trop court")]
    public void Une_valeur_qui_n_a_pas_l_air_d_un_ncc_se_signale(string ncc, string attendu)
    {
        var etat = Analyser(
            [Entete("1", "C1")],
            [Ligne("1", 100m)],
            Client("C1", ncc));

        Assert.Contains(attendu, Assert.Single(etat.Douteux).Pourquoi);
    }

    [Fact]
    public void Une_forme_inhabituelle_mais_plausible_n_est_pas_signalee()
    {
        // Le signalement porte sur des faits — un champ détourné, une valeur
        // trop courte — jamais sur un format décrété. Refuser ici une forme
        // légitime qu'on n'aurait pas prévue bloquerait un client réel.
        var etat = Analyser(
            [Entete("1", "C1"), Entete("2", "C2")],
            [Ligne("1", 100m), Ligne("2", 100m)],
            Client("C1", "CI-2026-000123"), Client("C2", "P0512345X"));

        Assert.Empty(etat.Douteux);
    }

    [Fact]
    public void La_campagne_ne_propose_jamais_de_ncc()
    {
        // Le test qui compte : rien dans le résultat ne doit ressembler à un
        // numéro proposé. Un NCC se demande au client, il ne se déduit pas.
        var etat = Analyser(
            [Entete("1", "C1"), Entete("2", "C2")],
            [Ligne("1", 100m), Ligne("2", 100m)],
            Client("C1"), Client("C2", "1432262S", "Même groupe"));

        var propose = Assert.Single(etat.Comptes);

        Assert.Equal("", propose.Telephone);
        Assert.DoesNotContain("1432262S", propose.Intitule);
        Assert.DoesNotContain("1432262S", propose.MoyenDeContact);
    }
}
