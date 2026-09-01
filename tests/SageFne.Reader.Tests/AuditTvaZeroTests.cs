using SageFne.Reader.Batch;
using SageFne.Reader.Audit;
using SageFne.Reader.Models.Sage;

namespace SageFne.Reader.Tests;

/// <summary>
/// L'audit expose des faits et ne conclut rien.
/// </summary>
/// <remarks>
/// Le piège serait de lui faire dire ce que Sage ne porte pas : TVAC et TVAD
/// valent tous deux 0 %, et aucun comptage ne les sépare. Ces tests vérifient
/// donc autant ce que l'audit dit que ce qu'il se garde de dire.
/// </remarks>
public class AuditTvaZeroTests
{
    private static SageDocumentHeader Entete(string piece, string tiers) => new()
    {
        Piece = piece,
        Domaine = 0,
        Type = 6,
        DocType = 6,
        Date = new DateTime(2025, 10, 22),
        Tiers = tiers,
    };

    private static SageDocumentLine Ligne(
        string piece, int rang, string article, decimal taux,
        decimal montantHT = 1000m, decimal quantite = 1m,
        string codeTva = "TVA", decimal airsi = 0m) => new()
    {
        Piece = piece,
        Domaine = 0,
        Type = 6,
        Ligne = rang,
        ArticleReference = article,
        Designation = $"Article {article}",
        Quantite = quantite,
        MontantHT = montantHT,
        CodeTaxe1 = taux == 0m ? "" : codeTva,
        Taxe1 = taux,
        CodeTaxe2 = airsi == 0m ? "" : "AIRSI",
        Taxe2 = airsi,
    };

    private static SageCustomer Client(string ctNum, string nom, string ncc = "") =>
        new() { CtNum = ctNum, Intitule = nom, Identifiant = ncc };

    private static AuditTvaZero Analyser(
        IEnumerable<SageDocumentHeader> entetes,
        IEnumerable<SageDocumentLine> lignes,
        IEnumerable<SageCustomer>? clients = null,
        Dictionary<string, string>? familles = null) =>
        AuditTvaZero.Analyser(
            [.. entetes],
            [.. lignes],
            (clients ?? []).ToDictionary(c => c.CtNum, StringComparer.OrdinalIgnoreCase),
            familles ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Un_dossier_sans_ligne_a_zero_ne_remonte_aucun_article()
    {
        var audit = Analyser(
            [Entete("1", "C1")],
            [Ligne("1", 1000, "A", 18m), Ligne("1", 2000, "B", 9m)]);

        Assert.Empty(audit.Articles);
        Assert.Equal(0m, audit.MontantHTTotal);
        Assert.Equal(2, audit.LignesExaminees);
    }

    [Fact]
    public void Un_article_jamais_vendu_taxe_ressort_comme_exclusif()
    {
        // La question 1 : cet article est-il toujours à 0 % ?
        var audit = Analyser(
            [Entete("1", "C1"), Entete("2", "C1")],
            [Ligne("1", 1000, "25SN001", 0m), Ligne("2", 1000, "25SN001", 0m)]);

        var article = Assert.Single(audit.Articles);
        Assert.Equal("25SN001", article.Reference);
        Assert.True(article.ExclusivementAZero);
        Assert.Empty(article.AutresTaux);
        Assert.Equal(2, article.LignesAZero);
        Assert.Equal(2, article.Factures);
        Assert.Single(audit.ArticlesExclusivementAZero);
        Assert.Empty(audit.ArticlesAPlusieursTaux);
    }

    [Fact]
    public void Un_article_vendu_aussi_taxe_expose_ses_autres_taux()
    {
        // La question 3, et la plus utile : si l'article part parfois à 18 %,
        // le 0 % ne tient pas à l'article.
        var audit = Analyser(
            [Entete("1", "C1"), Entete("2", "C2"), Entete("3", "C3")],
            [
                Ligne("1", 1000, "25SN001", 0m),
                Ligne("2", 1000, "25SN001", 18m),
                Ligne("3", 1000, "25SN001", 9m),
            ]);

        var article = Assert.Single(audit.Articles);
        Assert.False(article.ExclusivementAZero);
        Assert.Equal([9m, 18m], article.AutresTaux);
        Assert.Single(audit.ArticlesAPlusieursTaux);
        Assert.Empty(audit.ArticlesExclusivementAZero);
    }

    [Fact]
    public void Les_codes_de_taxe_des_lignes_a_zero_sont_rapportes_tels_quels()
    {
        // La question 5. L'AIRSI en position 2 sur une ligne sans TVA : c'est
        // exactement ce que porte le dossier réel.
        var audit = Analyser(
            [Entete("1", "C1")],
            [Ligne("1", 1000, "A", 0m, airsi: 1.5m)]);

        var code = Assert.Single(Assert.Single(audit.Articles).CodesObserves);
        Assert.Equal(2, code.Position);
        Assert.Equal("AIRSI", code.Code);
        Assert.Equal(1.5m, code.Taux);
    }

    [Fact]
    public void Une_ligne_a_zero_sans_aucun_code_le_montre()
    {
        var audit = Analyser([Entete("1", "C1")], [Ligne("1", 1000, "A", 0m)]);

        Assert.Empty(Assert.Single(audit.Articles).CodesObserves);
    }

    [Fact]
    public void L_AIRSI_ne_compte_pas_comme_de_la_TVA()
    {
        // Une ligne à AIRSI 1,5 % et sans TVA est une ligne à 0 % de TVA :
        // confondre les deux ferait disparaître de l'audit les lignes qu'il
        // doit précisément montrer.
        var audit = Analyser(
            [Entete("1", "C1")],
            [Ligne("1", 1000, "A", 0m, airsi: 1.5m)]);

        Assert.Single(audit.Articles);
    }

    [Fact]
    public void Les_clients_sont_nommes_avec_leur_NCC_quand_il_existe()
    {
        var audit = Analyser(
            [Entete("1", "4111SITA"), Entete("2", "4111SANS")],
            [Ligne("1", 1000, "A", 0m, montantHT: 500m), Ligne("2", 1000, "A", 0m, montantHT: 100m)],
            [Client("4111SITA", "SITA SARL", "1432262S"), Client("4111SANS", "SANS NCC")]);

        var clients = Assert.Single(audit.Articles).Clients;

        Assert.Equal(2, clients.Count);
        // Classés par montant : le plus gros d'abord.
        Assert.Equal("SITA SARL", clients[0].Nom);
        Assert.Equal("1432262S", clients[0].Ncc);
        Assert.Equal("", clients[1].Ncc);
    }

    [Fact]
    public void Un_client_inconnu_du_lot_garde_son_compte_pour_nom()
    {
        var audit = Analyser(
            [Entete("1", "4111INCONNU")],
            [Ligne("1", 1000, "A", 0m)]);

        var client = Assert.Single(Assert.Single(audit.Articles).Clients);
        Assert.Equal("4111INCONNU", client.Compte);
        Assert.Equal("4111INCONNU", client.Nom);
        Assert.Equal("", client.Ncc);
    }

    [Fact]
    public void Une_famille_jamais_taxee_se_distingue_d_une_famille_panachee()
    {
        // La question 2 et la question 4 : le 0 % tient-il à la famille ?
        var audit = Analyser(
            [Entete("1", "C1"), Entete("2", "C1"), Entete("3", "C1")],
            [
                Ligne("1", 1000, "A", 0m),   // famille 01
                Ligne("2", 1000, "B", 0m),   // famille 02
                Ligne("3", 1000, "C", 18m),  // famille 02 aussi
            ],
            familles: new(StringComparer.OrdinalIgnoreCase)
            {
                ["A"] = "01", ["B"] = "02", ["C"] = "02",
            });

        var un = audit.Familles.Single(f => f.Cle == "01");
        var deux = audit.Familles.Single(f => f.Cle == "02");

        Assert.True(un.ToutesLignesAZero);
        Assert.False(deux.ToutesLignesAZero);
        Assert.Equal(1, deux.LignesTaxees);
    }

    [Fact]
    public void Un_client_qui_n_achete_jamais_taxe_se_distingue()
    {
        var audit = Analyser(
            [Entete("1", "EXO"), Entete("2", "NORMAL"), Entete("3", "NORMAL")],
            [
                Ligne("1", 1000, "A", 0m),
                Ligne("2", 1000, "A", 0m),
                Ligne("3", 1000, "A", 18m),
            ],
            [Client("EXO", "Client exonéré"), Client("NORMAL", "Client ordinaire")]);

        Assert.True(audit.Clients.Single(c => c.Cle == "EXO").ToutesLignesAZero);
        Assert.False(audit.Clients.Single(c => c.Cle == "NORMAL").ToutesLignesAZero);
    }

    [Fact]
    public void Une_famille_sans_lignes_a_zero_ne_figure_pas()
    {
        var audit = Analyser(
            [Entete("1", "C1"), Entete("2", "C1")],
            [Ligne("1", 1000, "A", 0m), Ligne("2", 1000, "B", 18m)],
            familles: new(StringComparer.OrdinalIgnoreCase) { ["A"] = "01", ["B"] = "09" });

        Assert.DoesNotContain(audit.Familles, famille => famille.Cle == "09");
    }

    [Fact]
    public void Les_cumuls_portent_sur_les_seules_lignes_a_zero()
    {
        var audit = Analyser(
            [Entete("1", "C1"), Entete("2", "C1")],
            [
                Ligne("1", 1000, "A", 0m, montantHT: 300m, quantite: 3m),
                Ligne("1", 2000, "A", 0m, montantHT: 200m, quantite: 2m),
                Ligne("2", 1000, "A", 18m, montantHT: 999m, quantite: 9m),
            ]);

        var article = Assert.Single(audit.Articles);
        Assert.Equal(500m, article.MontantHTCumule);
        Assert.Equal(5m, article.QuantiteCumulee);
        Assert.Equal(500m, audit.MontantHTTotal);
        Assert.Equal(1, audit.NombreFacturesConcernees);
    }

    [Fact]
    public void Les_articles_sont_classes_par_montant_decroissant()
    {
        var audit = Analyser(
            [Entete("1", "C1")],
            [
                Ligne("1", 1000, "PETIT", 0m, montantHT: 10m),
                Ligne("1", 2000, "GROS", 0m, montantHT: 1000m),
            ]);

        Assert.Equal(["GROS", "PETIT"], audit.Articles.Select(a => a.Reference));
    }

    [Fact]
    public void Une_ligne_sans_article_ne_cree_pas_d_entree_fantome()
    {
        var audit = Analyser(
            [Entete("1", "C1")],
            [Ligne("1", 1000, "", 0m), Ligne("1", 2000, "A", 0m)]);

        Assert.Single(audit.Articles);
        Assert.Equal(1, audit.LignesExaminees);
    }

    [Fact]
    public void Les_pieces_exemples_sont_limitees_et_sans_doublon()
    {
        var entetes = Enumerable.Range(1, 8).Select(n => Entete($"{n}", "C1")).ToList();
        var lignes = entetes.SelectMany(e => new[]
        {
            Ligne(e.Piece, 1000, "A", 0m),
            Ligne(e.Piece, 2000, "A", 0m),
        }).ToList();

        var article = Assert.Single(Analyser(entetes, lignes).Articles);

        Assert.Equal(5, article.ExemplesPieces.Count);
        Assert.Equal(article.ExemplesPieces.Distinct().Count(), article.ExemplesPieces.Count);
        Assert.Equal(8, article.Factures);
    }

    [Fact]
    public void L_audit_ne_prononce_jamais_TVAC_ni_TVAD()
    {
        // La garantie demandée : exposer, jamais conclure. Aucun champ du
        // résultat ne porte de code d'exonération, et rien ne s'en approche.
        var audit = Analyser(
            [Entete("1", "C1")],
            [Ligne("1", 1000, "A", 0m, airsi: 1.5m)],
            [Client("C1", "Client")],
            new(StringComparer.OrdinalIgnoreCase) { ["A"] = "01" });

        var texte = System.Text.Json.JsonSerializer.Serialize(audit);

        Assert.DoesNotContain("TVAC", texte, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TVAD", texte, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exoneration", texte, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exonération", texte, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Le relevé complet d'un article, ventes taxées comprises.
/// </summary>
/// <remarks>
/// L'audit d'ensemble ne retient que les lignes à 0 %, ce qui suffit à
/// inventorier mais pas à trancher : « exclusivement à 0 % ou panaché ? »
/// demande de voir aussi ce qui est taxé.
/// </remarks>
public class DetailArticleTests
{
    private static SageDocumentHeader Entete(string piece, string tiers, int jour = 22) => new()
    {
        Piece = piece,
        Domaine = 0,
        Type = 6,
        DocType = 6,
        Date = new DateTime(2025, 10, jour),
        Tiers = tiers,
    };

    private static SageDocumentLine Ligne(
        string piece, int rang, string article, decimal taux,
        decimal montantHT = 1000m, decimal quantite = 1m, decimal airsi = 0m) => new()
    {
        Piece = piece,
        Domaine = 0,
        Type = 6,
        Ligne = rang,
        ArticleReference = article,
        Designation = $"Article {article}",
        Quantite = quantite,
        MontantHT = montantHT,
        CodeTaxe1 = taux == 0m ? "" : "TVA",
        Taxe1 = taux,
        CodeTaxe2 = airsi == 0m ? "" : "AIRSI",
        Taxe2 = airsi,
    };

    private static DetailArticle? Detailler(
        string reference,
        IEnumerable<SageDocumentHeader> entetes,
        IEnumerable<SageDocumentLine> lignes,
        IEnumerable<SageCustomer>? clients = null,
        Dictionary<string, string>? familles = null) =>
        DetailArticle.Construire(
            reference,
            [.. entetes],
            [.. lignes],
            (clients ?? []).ToDictionary(c => c.CtNum, StringComparer.OrdinalIgnoreCase),
            familles ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Un_article_absent_du_perimetre_ne_rend_rien()
    {
        var detail = Detailler("INCONNU", [Entete("1", "C1")], [Ligne("1", 1000, "A", 18m)]);

        Assert.Null(detail);
    }

    [Fact]
    public void Le_releve_montre_aussi_les_ventes_taxees()
    {
        // Ce que l'audit d'ensemble ne montre pas, et qui décide.
        var detail = Detailler(
            "25SN001",
            [Entete("1", "C1"), Entete("2", "C1"), Entete("3", "C1")],
            [
                Ligne("1", 1000, "25SN001", 0m),
                Ligne("2", 1000, "25SN001", 18m),
                Ligne("3", 1000, "25SN001", 9m),
            ]);

        Assert.NotNull(detail);
        Assert.Equal(3, detail.Occurrences.Count);
        Assert.Equal([0m, 9m, 18m], detail.Occurrences.Select(o => o.TauxTva).OrderBy(t => t));
    }

    [Fact]
    public void Un_article_panache_se_declare_tel()
    {
        var detail = Detailler(
            "25SN001",
            [Entete("1", "C1"), Entete("2", "C1")],
            [Ligne("1", 1000, "25SN001", 0m), Ligne("2", 1000, "25SN001", 18m)]);

        Assert.True(detail!.Panache);
        Assert.False(detail.ExclusivementAZero);
    }

    [Fact]
    public void Un_article_jamais_taxe_se_declare_exclusif()
    {
        var detail = Detailler(
            "25SN001",
            [Entete("1", "C1"), Entete("2", "C1")],
            [Ligne("1", 1000, "25SN001", 0m), Ligne("2", 1000, "25SN001", 0m)]);

        Assert.True(detail!.ExclusivementAZero);
        Assert.False(detail.Panache);
    }

    [Fact]
    public void Un_article_toujours_taxe_n_est_ni_exclusif_ni_panache()
    {
        var detail = Detailler("A", [Entete("1", "C1")], [Ligne("1", 1000, "A", 18m)]);

        Assert.False(detail!.ExclusivementAZero);
        Assert.False(detail.Panache);
    }

    [Fact]
    public void La_repartition_par_taux_compte_lignes_et_montants()
    {
        var detail = Detailler(
            "A",
            [Entete("1", "C1"), Entete("2", "C1"), Entete("3", "C1")],
            [
                Ligne("1", 1000, "A", 0m, montantHT: 100m),
                Ligne("2", 1000, "A", 0m, montantHT: 200m),
                Ligne("3", 1000, "A", 18m, montantHT: 50m),
            ]);

        var zero = detail!.ParTaux.Single(t => t.Taux == 0m);
        Assert.Equal(2, zero.Lignes);
        Assert.Equal(300m, zero.MontantHT);
        Assert.Equal(1, detail.ParTaux.Single(t => t.Taux == 18m).Lignes);

        // Le taux le plus fréquent d'abord : c'est lui qui donne la règle.
        Assert.Equal(0m, detail.ParTaux[0].Taux);
    }

    [Fact]
    public void Chaque_occurrence_porte_sa_piece_sa_date_son_client_et_son_NCC()
    {
        var detail = Detailler(
            "A",
            [Entete("1052", "4111GEMSCI")],
            [Ligne("1052", 1000, "A", 0m, montantHT: 500m, quantite: 4m)],
            [new SageCustomer { CtNum = "4111GEMSCI", Intitule = "GEMS-CI", Identifiant = "1010983N" }]);

        var occurrence = Assert.Single(detail!.Occurrences);

        Assert.Equal("1052", occurrence.Piece);
        Assert.Equal(new DateTime(2025, 10, 22), occurrence.Date);
        Assert.Equal("4111GEMSCI", occurrence.Compte);
        Assert.Equal("GEMS-CI", occurrence.Client);
        Assert.Equal("1010983N", occurrence.Ncc);
        Assert.Equal(4m, occurrence.Quantite);
        Assert.Equal(500m, occurrence.MontantHT);
    }

    [Fact]
    public void Les_trois_emplacements_de_taxe_sont_rapportes_par_occurrence()
    {
        var detail = Detailler(
            "A", [Entete("1", "C1")], [Ligne("1", 1000, "A", 18m, airsi: 1.5m)]);

        var codes = Assert.Single(detail!.Occurrences).Codes;

        Assert.Equal(2, codes.Count);
        Assert.Equal(("TVA", 18m), (codes[0].Code, codes[0].Taux));
        Assert.Equal(("AIRSI", 1.5m), (codes[1].Code, codes[1].Taux));
    }

    [Fact]
    public void Une_ligne_a_zero_sans_code_le_montre_aussi()
    {
        var detail = Detailler("A", [Entete("1", "C1")], [Ligne("1", 1000, "A", 0m)]);

        Assert.Empty(Assert.Single(detail!.Occurrences).Codes);
    }

    [Fact]
    public void L_AIRSI_seul_laisse_le_taux_de_TVA_a_zero()
    {
        // Le piège : compter l'AIRSI comme de la TVA ferait disparaître la
        // ligne de l'audit, et la déclarerait taxée à tort.
        var detail = Detailler("A", [Entete("1", "C1")], [Ligne("1", 1000, "A", 0m, airsi: 1.5m)]);

        Assert.Equal(0m, Assert.Single(detail!.Occurrences).TauxTva);
        Assert.True(detail.ExclusivementAZero);
    }

    [Fact]
    public void Les_occurrences_sont_classees_de_la_plus_recente_a_la_plus_ancienne()
    {
        var detail = Detailler(
            "A",
            [Entete("ancienne", "C1", jour: 1), Entete("recente", "C1", jour: 28)],
            [Ligne("ancienne", 1000, "A", 0m), Ligne("recente", 1000, "A", 0m)]);

        Assert.Equal(["recente", "ancienne"], detail!.Occurrences.Select(o => o.Piece));
    }

    [Fact]
    public void Les_factures_et_clients_sont_comptes_sans_doublon()
    {
        var detail = Detailler(
            "A",
            [Entete("1", "C1"), Entete("2", "C1"), Entete("3", "C2")],
            [
                Ligne("1", 1000, "A", 0m),
                Ligne("1", 2000, "A", 0m),
                Ligne("2", 1000, "A", 0m),
                Ligne("3", 1000, "A", 18m),
            ]);

        Assert.Equal(4, detail!.Occurrences.Count);
        Assert.Equal(3, detail.NombreFactures);
        Assert.Equal(2, detail.NombreClients);
    }

    [Fact]
    public void La_reference_se_compare_sans_egard_a_la_casse()
    {
        var detail = Detailler("25sn001", [Entete("1", "C1")], [Ligne("1", 1000, "25SN001", 0m)]);

        Assert.NotNull(detail);
        Assert.Single(detail.Occurrences);
    }

    [Fact]
    public void La_famille_est_reprise_quand_elle_est_connue()
    {
        var detail = Detailler(
            "A", [Entete("1", "C1")], [Ligne("1", 1000, "A", 0m)],
            familles: new(StringComparer.OrdinalIgnoreCase) { ["A"] = "01" });

        Assert.Equal("01", detail!.Famille);
    }

    [Fact]
    public void Le_releve_ne_prononce_jamais_TVAC_ni_TVAD()
    {
        var detail = Detailler(
            "A",
            [Entete("1", "C1"), Entete("2", "C1")],
            [Ligne("1", 1000, "A", 0m, airsi: 1.5m), Ligne("2", 1000, "A", 18m)],
            [new SageCustomer { CtNum = "C1", Intitule = "Client", Identifiant = "123X" }],
            new(StringComparer.OrdinalIgnoreCase) { ["A"] = "01" });

        var texte = System.Text.Json.JsonSerializer.Serialize(detail);

        Assert.DoesNotContain("TVAC", texte, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TVAD", texte, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exonération", texte, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Les filtres d'affichage de l'audit.</summary>
public class FiltresAuditTests
{
    [Fact]
    public void Les_trois_filtres_se_lisent()
    {
        var ligne = CommandLine.Parse(
            ["audit-tva-zero", "--article", "25SN001", "--famille", "01", "--client", "4111SOGEL"]);

        Assert.Equal(Verbe.AuditTvaZero, ligne.Verbe);
        Assert.Equal("25SN001", ligne.Article);
        Assert.Equal("01", ligne.Famille);
        Assert.Equal("4111SOGEL", ligne.Client);
        Assert.True(ligne.AuditFiltre);
    }

    [Fact]
    public void Sans_filtre_l_audit_n_est_pas_restreint()
    {
        var ligne = CommandLine.Parse(["audit-tva-zero"]);

        Assert.False(ligne.AuditFiltre);
        Assert.Null(ligne.Article);
    }

    [Theory]
    [InlineData("--article")]
    [InlineData("--famille")]
    [InlineData("--client")]
    public void Un_filtre_sans_valeur_est_refuse(string option)
    {
        var ligne = CommandLine.Parse(["audit-tva-zero", option]);

        Assert.NotEmpty(ligne.Erreurs);
    }

    [Fact]
    public void L_audit_lit_tout_le_dossier_meme_filtre()
    {
        // Le filtre réduit l'affichage, jamais la lecture : un article se juge
        // sur toutes ses ventes, pas sur les cinq cents premières pièces.
        var ligne = CommandLine.Parse(["audit-tva-zero", "--article", "25SN001"]);

        Assert.Equal(2000, ligne.Query.Limite);
    }
}

/// <summary>
/// Ce que le dossier réel a corrigé dans l'audit lui-même.
/// </summary>
public class AuditFaitsObservesTests
{
    private static SageDocumentHeader Entete(string piece, string tiers) => new()
    {
        Piece = piece, Domaine = 0, Type = 6, DocType = 6,
        Date = new DateTime(2025, 10, 22), Tiers = tiers,
    };

    private static SageDocumentLine Ligne(
        string piece, int rang, string article, decimal taux,
        string codeTva = "TVA", decimal airsi = 0m, int emplacementAirsi = 2) => new()
    {
        Piece = piece, Domaine = 0, Type = 6, Ligne = rang,
        ArticleReference = article, Designation = $"Article {article}",
        Quantite = 1m, MontantHT = 1000m,
        CodeTaxe1 = emplacementAirsi == 1 && airsi != 0m ? "AIRSI" : (taux == 0m && codeTva == "" ? "" : codeTva),
        Taxe1 = emplacementAirsi == 1 && airsi != 0m ? airsi : taux,
        CodeTaxe2 = emplacementAirsi == 2 && airsi != 0m ? "AIRSI" : "",
        Taxe2 = emplacementAirsi == 2 ? airsi : 0m,
    };

    private static AuditTvaZero Analyser(
        IEnumerable<SageDocumentHeader> entetes, IEnumerable<SageDocumentLine> lignes) =>
        AuditTvaZero.Analyser(
            [.. entetes], [.. lignes],
            new Dictionary<string, SageCustomer>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Le_compte_de_clients_ne_vient_pas_de_la_liste_tronquee()
    {
        // Sur le dossier réel, l'audit annonçait « 10 client(s) » là où il y en
        // avait 13 : le plafond d'affichage se lisait comme un fait.
        var entetes = Enumerable.Range(1, 13).Select(n => Entete($"{n}", $"C{n}")).ToList();
        var lignes = entetes.Select(e => Ligne(e.Piece, 1000, "A", 0m, codeTva: "")).ToList();

        var article = Assert.Single(Analyser(entetes, lignes).Articles);

        Assert.Equal(13, article.NombreClients);
        Assert.Equal(10, article.Clients.Count);
    }

    [Fact]
    public void Les_lignes_sans_aucun_code_sont_comptees_a_part()
    {
        // Elles n'apparaissent dans aucun code observé, par construction : sans
        // ce compte, le détail semble démentir le nombre de lignes.
        var audit = Analyser(
            [Entete("1", "C1"), Entete("2", "C1"), Entete("3", "C1")],
            [
                Ligne("1", 1000, "A", 0m, codeTva: ""),
                Ligne("2", 1000, "A", 0m, codeTva: ""),
                Ligne("3", 1000, "A", 0m, codeTva: "TVA"),
            ]);

        var article = Assert.Single(audit.Articles);

        Assert.Equal(3, article.LignesAZero);
        Assert.Equal(2, article.LignesSansAucunCode);
        Assert.Equal(1, article.CodesObserves.Sum(code => code.Lignes));
    }

    [Fact]
    public void Une_ligne_portant_le_code_TVA_a_zero_est_bien_a_zero()
    {
        // Le dossier réel porte « DL_CodeTaxe1 = TVA, DL_Taxe1 = 0 » sur des
        // dizaines de lignes : un code présent ne veut pas dire un taux.
        var audit = Analyser([Entete("1", "C1")], [Ligne("1", 1000, "A", 0m, codeTva: "TVA")]);

        var article = Assert.Single(audit.Articles);
        Assert.Equal(0, article.LignesSansAucunCode);

        var code = Assert.Single(article.CodesObserves);
        Assert.Equal(("TVA", 0m), (code.Code, code.Taux));
    }

    [Fact]
    public void L_AIRSI_est_reconnu_en_position_1_comme_en_position_2()
    {
        // Le dossier réel le porte aux deux endroits. S'attendre à la position 2
        // ferait lire 1,5 % de TVA là où il n'y en a pas.
        var enUn = Analyser([Entete("1", "C1")], [Ligne("1", 1000, "A", 0m, airsi: 1.5m, emplacementAirsi: 1)]);
        var enDeux = Analyser([Entete("1", "C1")], [Ligne("1", 1000, "A", 0m, codeTva: "", airsi: 1.5m)]);

        Assert.Single(enUn.Articles);
        Assert.Single(enDeux.Articles);
        Assert.Equal(1, Assert.Single(enUn.Articles).CodesObserves.Single().Position);
        Assert.Equal(2, Assert.Single(enDeux.Articles).CodesObserves.Single().Position);
    }

    [Theory]
    [InlineData(1, "jamais taxé, mais sur 1 ligne(s) seulement")]
    [InlineData(4, "jamais taxé, mais sur 4 ligne(s) seulement")]
    [InlineData(5, "jamais taxé")]
    [InlineData(50, "jamais taxé")]
    public void Une_lecture_dit_sur_combien_d_observations_elle_repose(int lignes, string attendu)
    {
        // Un client vu une fois n'est pas un client exonéré : c'est un client
        // dont on ne sait rien. Le dire du même ton ferait paramétrer une règle
        // sur une observation unique.
        var regroupement = new RegroupementAZero("C", "Client", lignes, 0, 1000m);

        Assert.Equal(attendu, regroupement.Lecture);
    }

    [Fact]
    public void Un_regroupement_panache_le_reste_quel_que_soit_le_volume()
    {
        Assert.Equal("panaché", new RegroupementAZero("C", "Client", 1, 1, 0m).Lecture);
        Assert.Equal("panaché", new RegroupementAZero("C", "Client", 500, 1, 0m).Lecture);
    }
}
