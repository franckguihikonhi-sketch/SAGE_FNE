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

    /// <remarks>
    /// Sans téléphone par défaut : la DGI l'exige au même titre que le NCC, et
    /// un client complet doit le dire explicitement.
    /// </remarks>
    private static SageCustomer Client(
        string ctNum, string ncc = "", string intitule = "Client",
        string telephone = "", string email = "", short typeNif = 0) => new()
    {
        CtNum = ctNum,
        Intitule = intitule,
        Identifiant = ncc,
        Telephone = telephone,
        Email = email,
        TypeNif = typeNif,
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

        Assert.Equal(1, etat.FacturesIncompletes);
        Assert.Equal(0, etat.FacturesCouvertes);
        Assert.Equal("C1", Assert.Single(etat.Comptes).CtNum);
    }

    [Fact]
    public void Un_client_complet_n_y_entre_pas()
    {
        var etat = Analyser(
            [Entete("1", "C1")],
            [Ligne("1", 1000m)],
            Client("C1", "1432262S", telephone: "0700000000"));

        Assert.Equal(0, etat.FacturesIncompletes);
        Assert.Equal(1, etat.FacturesCouvertes);
        Assert.Empty(etat.Comptes);
        Assert.Equal(1, etat.ComptesRenseignes);
    }

    [Fact]
    public void Un_client_avec_ncc_mais_sans_telephone_reste_dans_la_campagne()
    {
        // Le téléphone est obligatoire côté DGI. Un compte qui n'a que son NCC
        // ne fait pas partir ses factures : les sortir de la liste au motif que
        // le NCC est là laisserait croire au travail fini.
        var etat = Analyser(
            [Entete("1", "C1")],
            [Ligne("1", 1000m)],
            Client("C1", "1432262S"));

        var compte = Assert.Single(etat.Comptes);

        Assert.False(compte.SansNcc);
        Assert.True(compte.SansTelephone);
        Assert.Equal("tél.", compte.Manques);
        Assert.Equal(0, etat.ComptesSansNcc);
        Assert.Equal(1, etat.ComptesSansTelephone);
    }

    [Fact]
    public void Les_deux_manques_se_comptent_separement()
    {
        var etat = Analyser(
            [Entete("1", "C1"), Entete("2", "C2"), Entete("3", "C3")],
            [Ligne("1", 100m), Ligne("2", 100m), Ligne("3", 100m)],
            Client("C1"),
            Client("C2", "1432262S"),
            Client("C3", telephone: "0700000000"));

        Assert.Equal(2, etat.ComptesSansNcc);
        Assert.Equal(2, etat.ComptesSansTelephone);
        Assert.Equal(1, etat.ComptesSansLesDeux);

        var parCompte = etat.Comptes.ToDictionary(compte => compte.CtNum);
        Assert.Equal("NCC + tél.", parCompte["C1"].Manques);
        Assert.Equal("tél.", parCompte["C2"].Manques);
        Assert.Equal("NCC", parCompte["C3"].Manques);
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

        Assert.Equal(1, etat.FacturesIncompletes);
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
        Assert.Equal(750_000m, etat.MontantIncomplet);
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
        Assert.Equal(1, etat.FacturesIncompletes);
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

        Assert.Equal(12, etat.FacturesIncompletes);
        Assert.Equal(1, etat.ComptesPour(0.5m));
        Assert.Equal(5, etat.ComptesPour(0.8m));
    }

    [Fact]
    public void Sans_manque_aucun_appel_n_est_a_passer()
    {
        var etat = Analyser(
            [Entete("1", "C1")],
            [Ligne("1", 1_000m)],
            Client("C1", "1432262S", telephone: "0700000000"));

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

    // --- Les deux classements ne sont pas le même --------------------------

    [Fact]
    public void Le_classement_par_montant_et_par_nombre_designent_des_comptes_differents()
    {
        // Le cas du dossier réel, en petit : un compte porte le montant, un
        // autre porte les factures. Annoncer « un compte suffit » sous un
        // tableau classé par montant désignait la mauvaise ligne.
        var entetes = new List<SageDocumentHeader> { Entete("G1", "GROS") };
        var lignes = new List<SageDocumentLine> { Ligne("G1", 900_000m) };

        for (var rang = 1; rang <= 9; rang++)
        {
            entetes.Add(Entete($"V{rang}", "VOLUME"));
            lignes.Add(Ligne($"V{rang}", 1_000m));
        }

        var etat = Analyser(entetes, lignes, Client("GROS"), Client("VOLUME"));

        Assert.Equal("GROS", etat.Comptes[0].CtNum);
        Assert.Equal("VOLUME", etat.ParNombre[0].CtNum);

        Assert.Equal(1, etat.ComptesPourMontant(0.5m));
        Assert.Equal(1, etat.ComptesPour(0.5m));

        // Et ce n'est pas le même compte derrière ces deux « 1 ».
        Assert.NotEqual(etat.Comptes[0].CtNum, etat.ParNombre[0].CtNum);
    }

    // --- Ce qui s'écarte de la forme du dossier -----------------------------

    /// <summary>Les huit NCC réellement saisis dans le dossier.</summary>
    private static SageCustomer[] HuitNccReels() =>
    [
        Client("C1", "5011806N"), Client("C2", "1529193P"), Client("C3", "1010983N"),
        Client("C4", "0803099K"),
        Client("C5", "2403386 B", "Espace en trop"),
        Client("C6", "1000221588", "Dix chiffres"),
        Client("C7", "N° CI 000922575", "Libellé dans le champ"),
        Client("C8", "163778S", "Un chiffre de moins"),
    ];

    private static EtatCampagneNcc HuitComptes()
    {
        var clients = HuitNccReels();
        return Analyser(
            clients.Select((client, rang) => Entete($"{rang}", client.CtNum)),
            clients.Select((_, rang) => Ligne($"{rang}", 100m)),
            clients);
    }

    [Fact]
    public void Un_espace_en_trop_se_reconnait_comme_tel()
    {
        // « 2403386 B » n'est pas une autre forme : c'est la forme majoritaire
        // avec une frappe de trop. Le dire évite de croire à deux conventions.
        var ecart = Assert.Single(HuitComptes().Ecarts, entree => entree.CtNum == "C5");

        Assert.Contains("espace", ecart.Observation);
        Assert.Contains("2403386B", ecart.Observation);
    }

    [Fact]
    public void Une_forme_isolee_se_compare_a_la_majoritaire()
    {
        var etat = HuitComptes();

        Assert.Equal("9999999A", etat.FormeDominante?.Gabarit);

        foreach (var compte in new[] { "C6", "C7", "C8" })
        {
            var ecart = Assert.Single(etat.Ecarts, entree => entree.CtNum == compte);
            Assert.Contains("9999999A", ecart.Observation);
        }
    }

    [Fact]
    public void Les_ncc_de_la_forme_majoritaire_ne_sont_pas_signales()
    {
        var etat = HuitComptes();

        Assert.DoesNotContain(etat.Ecarts, ecart => ecart.CtNum is "C1" or "C2" or "C3" or "C4");
        Assert.Equal(4, etat.Ecarts.Count);
    }

    [Fact]
    public void Sans_forme_majoritaire_rien_ne_se_compare()
    {
        // Trois NCC de trois formes : aucune ne fait référence, et prétendre le
        // contraire signalerait les trois comme déviants de rien.
        var etat = Analyser(
            [Entete("1", "C1"), Entete("2", "C2"), Entete("3", "C3")],
            [Ligne("1", 100m), Ligne("2", 100m), Ligne("3", 100m)],
            Client("C1", "1432262S"), Client("C2", "1000221588"), Client("C3", "CI-2026-0001"));

        Assert.Null(etat.FormeDominante);
        Assert.Empty(etat.Ecarts);
    }

    [Fact]
    public void Un_ecart_de_forme_n_est_pas_une_valeur_douteuse()
    {
        // Les deux listes ne disent pas la même chose. « N° CI 000922575 » est
        // inhabituel dans ce dossier ; il n'est pas pour autant impossible.
        var etat = HuitComptes();

        Assert.NotEmpty(etat.Ecarts);
        Assert.Empty(etat.Douteux);
    }

    [Fact]
    public void Le_type_de_nif_est_repris_tel_quel()
    {
        // Sage le porte, personne ne le lisait. Aucune interprétation ici : la
        // valeur passe, et c'est au dossier de dire ce qu'elle vaut.
        var etat = Analyser(
            [Entete("1", "C1")], [Ligne("1", 100m)], Client("C1", typeNif: 3));

        Assert.Equal(3, Assert.Single(etat.Comptes).TypeNif);
    }

    [Fact]
    public void Un_type_de_nif_constant_ne_distingue_rien()
    {
        // Le dossier réel porte 0 sur les 74 comptes. Aligner soixante-quatorze
        // zéros dans une colonne les ferait lire comme une donnée, alors que
        // c'est une absence : le champ n'est pas renseigné, voilà tout.
        var etat = Analyser(
            [Entete("1", "C1"), Entete("2", "C2")],
            [Ligne("1", 100m), Ligne("2", 100m)],
            Client("C1"), Client("C2"));

        Assert.True(etat.TypeNifConstant);
    }

    [Fact]
    public void Un_type_de_nif_qui_varie_distingue_quelque_chose()
    {
        var etat = Analyser(
            [Entete("1", "C1"), Entete("2", "C2")],
            [Ligne("1", 100m), Ligne("2", 100m)],
            Client("C1", typeNif: 1), Client("C2", typeNif: 3));

        Assert.False(etat.TypeNifConstant);
    }

    [Fact]
    public void Un_seul_compte_ne_fait_pas_une_constante()
    {
        // Une valeur unique n'est pas un champ constant : elle ne dit rien du
        // dossier, et l'annoncer comme un fait serait conclure sur un exemple.
        var etat = Analyser([Entete("1", "C1")], [Ligne("1", 100m)], Client("C1"));

        Assert.False(etat.TypeNifConstant);
    }

    [Fact]
    public void La_campagne_ne_propose_jamais_de_ncc()
    {
        // Le test qui compte : rien dans le résultat ne doit ressembler à un
        // numéro proposé. Un NCC se demande au client, il ne se déduit pas.
        var etat = Analyser(
            [Entete("1", "C1"), Entete("2", "C2")],
            [Ligne("1", 100m), Ligne("2", 100m)],
            Client("C1"), Client("C2", "1432262S", "Même groupe", telephone: "0700000000"));

        var propose = Assert.Single(etat.Comptes);

        Assert.Equal("", propose.Telephone);
        Assert.DoesNotContain("1432262S", propose.Intitule);
        Assert.DoesNotContain("1432262S", propose.MoyenDeContact);
    }
}
