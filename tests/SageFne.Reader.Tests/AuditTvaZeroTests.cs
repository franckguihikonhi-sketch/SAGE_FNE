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
