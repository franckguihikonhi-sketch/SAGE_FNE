using SageFne.Installeur;

namespace SageFne.Installeur.Tests;

/// <summary>
/// L'installeur livré au client : ce qu'il refuse d'installer, et pourquoi.
/// </summary>
/// <remarks>
/// Il tourne sur le poste d'un client, sans dépôt Git, sans SDK, sans nous
/// derrière. Tout ce qu'il refuse, il doit le refuser <b>avant</b> la première
/// écriture : une installation qui s'arrête au milieu laisse une machine dans
/// un état que personne n'a voulu — c'est arrivé au script PowerShell, qui
/// effaçait les variables machine puis échouait sur une clé absente.
/// </remarks>
public class InstalleurTests
{
    private static Demande Complete() => new()
    {
        ChaineSage = "Server=SRV;Database=BIJOU;Integrated Security=True;",
        CleFne = "cle-reelle-de-la-dgi",
        PointDeVente = "FISH-AFRIC",
        Etablissement = "FISH-AFRIC",
    };

    private static IReadOnlyList<string> Empechements(Demande demande) =>
        Controles.Empechements(demande, agentEmbarque: true);

    [Fact]
    public void Une_demande_complete_n_empeche_rien() => Assert.Empty(Empechements(Complete()));

    [Fact]
    public void Un_executable_sans_charge_utile_refuse_d_installer()
    {
        // Un binaire de développement, compilé sans l'agent, ne doit pas
        // déposer un dossier vide et enregistrer un service qui ne démarrera
        // jamais.
        var manques = Controles.Empechements(Complete(), agentEmbarque: false);

        Assert.Contains(manques, m => m.Contains("ne porte pas l'agent"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("A_COMPLETER")]
    [InlineData("Server=SERVEUR_SQL;Database=VOTRE_BASE")]
    public void Une_chaine_Sage_absente_ou_au_gabarit_arrete_tout(string chaine)
    {
        // Sans elle, la lecture retombe sur le jeu d'essai — et une facture
        // inventée est déjà réellement partie à la DGI pour cette raison.
        Assert.NotEmpty(Empechements(Complete() with { ChaineSage = chaine }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("A_COMPLETER")]
    [InlineData("VOTRE_POINT")]
    [InlineData("<point-de-vente>")]
    public void Un_point_de_vente_absent_ou_au_gabarit_arrete_tout(string valeur)
    {
        // Aucun contrôle de pièce ne peut le voir : la facture partirait
        // irréprochable et la DGI répondrait « Establishment is invalid ».
        var manques = Empechements(Complete() with { PointDeVente = valeur });

        Assert.Contains(manques, m => m.Contains("point de vente"));
    }

    [Fact]
    public void Un_registre_dans_un_profil_utilisateur_est_refuse()
    {
        // Le service tourne sous un autre compte : il y écrirait un second
        // registre. Deux registres pour une seule vérité ont déjà fait
        // certifier deux fois la même facture.
        var manques = Empechements(Complete() with
        {
            Registre = @"C:\Users\Samuel\AppData\Roaming\SageFne\certifications.json",
        });

        Assert.Contains(manques, m => m.Contains("profil utilisateur"));
    }

    [Theory]
    [InlineData(@"C:\ProgramData\SageFne\certifications.json")]
    [InlineData(@"D:\SageFne\certifications.json")]
    public void Un_registre_hors_profil_passe(string chemin) =>
        Assert.Empty(Empechements(Complete() with { Registre = chemin }));

    [Fact]
    public void Le_SaaS_demande_a_moitie_est_refuse()
    {
        // Les trois valeurs vont ensemble. Deux sur trois laisseraient le
        // miroir éteint sans que l'installateur le sache.
        var manques = Empechements(Complete() with { SupabaseUrl = "https://x.supabase.co" });

        Assert.Equal(2, manques.Count);
    }

    [Fact]
    public void Le_SaaS_complet_passe() =>
        Assert.Empty(Empechements(Complete() with
        {
            SupabaseUrl = "https://abcdefgh.supabase.co",
            SupabaseCle = "service-role",
            Dossier = "22222222-2222-2222-2222-222222222222",
        }));

    [Fact]
    public void Le_SaaS_non_demande_ne_manque_a_personne() =>
        Assert.Empty(Empechements(Complete()));

    // --- Les arguments ------------------------------------------------------

    [Fact]
    public void Les_valeurs_se_donnent_en_arguments_pour_un_deploiement_scripte()
    {
        var analyse = LigneDeCommande.Lire([
            "--silencieux",
            "--sage", "Server=SRV;Database=BIJOU;Integrated Security=True;",
            "--cle-fne", "abc",
            "--point-de-vente", "FISH-AFRIC",
            "--etablissement", "FISH-AFRIC",
        ]);

        Assert.Empty(analyse.Erreurs);
        Assert.True(analyse.Demande.Silencieux);
        Assert.Equal("FISH-AFRIC", analyse.Demande.PointDeVente);
    }

    [Fact]
    public void L_essai_est_le_defaut_et_la_production_se_demande()
    {
        Assert.False(LigneDeCommande.Lire([]).Demande.Production);
        Assert.True(LigneDeCommande.Lire(["--production"]).Demande.Production);
    }

    [Fact]
    public void Une_option_inconnue_arrete_avant_toute_question()
    {
        // Une faute de frappe sur « --point-de-vent » installerait un poste
        // sans point de vente si elle était ignorée.
        Assert.NotEmpty(LigneDeCommande.Lire(["--point-de-vent", "X"]).Erreurs);
    }

    [Fact]
    public void Une_option_sans_valeur_est_signalee()
    {
        // « --cle-fne --production » prendrait « --production » pour la clé.
        var analyse = LigneDeCommande.Lire(["--cle-fne", "--production"]);

        Assert.NotEmpty(analyse.Erreurs);
        Assert.Equal("", analyse.Demande.CleFne);
    }

    [Fact]
    public void Les_chemins_ont_des_defauts_utilisables_tels_quels()
    {
        var demande = LigneDeCommande.Lire([]).Demande;

        Assert.Equal(@"C:\SageFne\agent", demande.Destination);
        Assert.Equal(@"C:\ProgramData\SageFne\certifications.json", demande.Registre);
        Assert.False(Controles.DansUnProfil(demande.Registre));
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--aide")]
    [InlineData("/?")]
    public void L_aide_se_demande_de_plusieurs_facons(string mot) =>
        Assert.True(LigneDeCommande.Lire([mot]).AideDemandee);

    [Fact]
    public void L_aide_ne_montre_aucun_gabarit_entre_chevrons()
    {
        // Sixième fois que ce projet trébuche dessus : PowerShell refuse les
        // chevrons, et une valeur qui en porte s'installe pourtant en silence
        // quand la commande passe.
        Assert.DoesNotContain("<", LigneDeCommande.Aide);
    }

    [Fact]
    public void L_aide_dit_que_le_compte_SQL_est_en_lecture_seule()
    {
        // C'est la garantie la plus importante du produit, et celle qu'un
        // installateur pressé donnera de travers s'il ne la lit pas.
        Assert.Contains("LECTURE SEULE", LigneDeCommande.Aide);
    }

    [Fact]
    public void L_aide_dit_que_le_service_demarre_en_Manual()
    {
        // Un poste installé qui se mettrait à certifier tout seul serait la
        // pire surprise possible chez un client.
        Assert.Contains("Manual", LigneDeCommande.Aide);
    }

    // --- La désinstallation -------------------------------------------------

    [Fact]
    public void La_desinstallation_se_demande_explicitement()
    {
        Assert.False(LigneDeCommande.Lire([]).Demande.Desinstaller);
        Assert.True(LigneDeCommande.Lire(["--desinstaller"]).Demande.Desinstaller);
    }

    [Fact]
    public void L_aide_dit_que_le_registre_survit_a_la_desinstallation()
    {
        // Il est la seule mémoire de ce qui a été déclaré à la DGI. L'effacer
        // parce qu'on retire un logiciel serait perdre la trace d'un fait
        // fiscal, et une facture certifiée ne s'annule que par un avoir.
        var aide = LigneDeCommande.Aide;

        Assert.Contains("--desinstaller", aide);
        Assert.Contains("NI le registre", aide);
    }

    // --- Reconnaître qu'un poste appartient déjà à un autre client ----------

    private const string Pose = """
        { "Fne": {
            "PointOfSale": "FISH-AFRIC",
            "Establishment": "FISH-AFRIC",
            "CertificationLedgerPath": "C:\\ProgramData\\SageFne\\certifications.json" } }
        """;

    [Fact]
    public void Une_reinstallation_sur_le_meme_client_ne_dit_rien()
    {
        // Banale, et elle doit rester silencieuse : un avertissement qui sort à
        // chaque fois est un avertissement qu'on apprend à ignorer.
        Assert.Null(Reconnaissance.Avertissement(Reconnaissance.Lire(Pose), Complete()));
    }

    [Fact]
    public void Installer_par_dessus_un_autre_client_se_dit_tout_haut()
    {
        // Le vrai danger d'un déploiement multi-clients. Chaque client a son
        // propre accès FNE : se tromper de poste ferait certifier les factures
        // de l'un sous le NCC de l'autre.
        var alerte = Reconnaissance.Avertissement(
            Reconnaissance.Lire(Pose),
            Complete() with { PointDeVente = "SOCOPRIX", Etablissement = "SOCOPRIX" });

        Assert.NotNull(alerte);
        Assert.Contains("FISH-AFRIC", alerte);
        Assert.Contains("SOCOPRIX", alerte);
        Assert.Contains("mauvais NCC", alerte);
    }

    [Fact]
    public void L_avertissement_rappelle_de_mettre_l_ancien_registre_de_cote()
    {
        // Il porte les certifications de l'ancien client : l'effacer perdrait
        // la seule trace de ce qui a été déclaré à la DGI pour lui.
        var alerte = Reconnaissance.Avertissement(
            Reconnaissance.Lire(Pose), Complete() with { PointDeVente = "AUTRE", Etablissement = "AUTRE" });

        Assert.Contains("certifications.json", alerte);
        Assert.Contains("n'effacez rien", alerte);
    }

    [Fact]
    public void Un_poste_neuf_n_avertit_de_rien()
    {
        Assert.Null(Reconnaissance.Avertissement(Reconnaissance.Lire(""), Complete()));
        Assert.Null(Reconnaissance.Avertissement(null, Complete()));
    }

    [Fact]
    public void Un_gabarit_en_place_n_est_pas_une_identite()
    {
        // « A_COMPLETER » n'appartient à personne : une installation par-dessus
        // n'est pas un changement de client.
        var gabarit = """{ "Fne": { "PointOfSale": "A_COMPLETER", "Establishment": "A_COMPLETER" } }""";

        Assert.Null(Reconnaissance.Avertissement(Reconnaissance.Lire(gabarit), Complete()));
    }

    [Fact]
    public void Un_appsettings_abime_ne_fait_pas_croire_a_un_autre_client() =>
        Assert.Null(Reconnaissance.Lire("pas du json"));

    // --- La vérification sur place -------------------------------------------

    [Fact]
    public void La_verification_se_demande_explicitement()
    {
        Assert.False(LigneDeCommande.Lire([]).Demande.Verifier);
        Assert.True(LigneDeCommande.Lire(["--verifier"]).Demande.Verifier);
    }

    [Fact]
    public void L_aide_dit_de_verifier_chez_le_client_avant_d_installer()
    {
        var aide = LigneDeCommande.Aide;

        Assert.Contains("--verifier", aide);
        Assert.Contains("CHEZ LE CLIENT", aide);
        Assert.Contains("mauvais NCC", aide);
    }
}
