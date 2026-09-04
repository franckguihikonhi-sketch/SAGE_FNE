using SageFne.Core.Mapping;
using SageFne.Core.Models.Sage;

namespace SageFne.Core.Audit;

/// <param name="Nom">Intitulé du client, ou son compte à défaut.</param>
/// <param name="Ncc">Vide quand la fiche n'en porte pas.</param>
public sealed record ClientAZero(string Compte, string Nom, string Ncc, int Lignes, decimal MontantHT);

/// <param name="Position">1, 2 ou 3 — l'emplacement de taxe dans F_DOCLIGNE.</param>
public sealed record CodeTaxeObserve(int Position, string Code, decimal Taux, int Lignes);

/// <summary>
/// Ce qu'un article montre, quand il est vendu sans TVA.
/// </summary>
public sealed record ArticleAZero
{
    public required string Reference { get; init; }
    public string Designation { get; init; } = "";
    public string Famille { get; init; } = "";

    public int LignesAZero { get; init; }
    public int Factures { get; init; }
    public decimal QuantiteCumulee { get; init; }
    public decimal MontantHTCumule { get; init; }

    /// <summary>Ce que portent les emplacements de taxe des lignes à 0 %.</summary>
    public IReadOnlyList<CodeTaxeObserve> CodesObserves { get; init; } = [];

    public IReadOnlyList<ClientAZero> Clients { get; init; } = [];

    /// <summary>
    /// Les taux auxquels ce même article est vendu ailleurs dans le dossier.
    /// </summary>
    /// <remarks>
    /// La question qui décide : un article toujours à 0 % relève d'une règle
    /// attachée à l'article ; un article tantôt à 0 %, tantôt à 9 ou 18 %,
    /// relève d'autre chose — du client, de l'opération, ou d'une saisie.
    /// </remarks>
    public IReadOnlyList<decimal> AutresTaux { get; init; } = [];

    public IReadOnlyList<string> ExemplesPieces { get; init; } = [];

    /// <summary>Vrai quand cet article n'est jamais vendu autrement qu'à 0 %.</summary>
    public bool ExclusivementAZero => AutresTaux.Count == 0;

    /// <summary>
    /// Combien de clients distincts, tous comptés.
    /// </summary>
    /// <remarks>
    /// Renseigné à part, et non déduit de <see cref="Clients"/> : cette liste
    /// est tronquée pour l'affichage, et en tirer un compte annonçait « 10
    /// clients » là où il y en avait 13. Un plafond d'affichage n'est pas un
    /// fait.
    /// </remarks>
    public int NombreClients { get; init; }

    /// <summary>
    /// Lignes à 0 % dont les trois emplacements de taxe sont vides.
    /// </summary>
    /// <remarks>
    /// Le cas dominant sur le dossier réel, et invisible dans le détail des
    /// codes : une ligne sans code n'y apparaît par construction pas. Il faut
    /// donc la compter à part, sans quoi le total des codes observés semble
    /// démentir le nombre de lignes.
    /// </remarks>
    public int LignesSansAucunCode { get; init; }
}

/// <param name="ToutesLignesAZero">
/// Vrai quand aucune ligne de ce regroupement n'est taxée : le 0 % y est la
/// règle, non l'exception.
/// </param>
public sealed record RegroupementAZero(
    string Cle,
    string Libelle,
    int LignesAZero,
    int LignesTaxees,
    decimal MontantHTAZero)
{
    public bool ToutesLignesAZero => LignesTaxees == 0;

    /// <summary>
    /// En dessous, « jamais taxé » ne décrit pas une habitude.
    /// </summary>
    /// <remarks>
    /// Un client qui a acheté une fois, sans TVA, n'est pas un client exonéré :
    /// c'est un client dont on ne sait rien. Affirmer les deux du même ton
    /// ferait paramétrer une règle sur une observation unique.
    /// </remarks>
    public const int ObservationsSuffisantes = 5;

    /// <summary>Ce que ce regroupement autorise à dire, et rien de plus.</summary>
    public string Lecture => (ToutesLignesAZero, LignesAZero) switch
    {
        (true, < ObservationsSuffisantes) =>
            $"jamais taxé, mais sur {LignesAZero} ligne(s) seulement",
        (true, _) => "jamais taxé",
        _ => "panaché",
    };
}

/// <summary>
/// Inventaire des ventes à 0 % de TVA, sans aucune conclusion fiscale.
/// </summary>
/// <remarks>
/// <b>Cette analyse ne classe rien.</b> Elle ne dit ni <c>TVAC</c> ni
/// <c>TVAD</c>, et ne le peut pas : les deux valent 0 %, et Sage ne porte pas
/// la différence. Elle expose des faits — quels articles, quelles familles,
/// quels clients, quels codes de taxe — pour que le fondement juridique soit
/// établi par qui le connaît, puis déclaré dans <c>Fne:ZeroVat</c>.
///
/// La distinction qu'elle sert à éclairer est celle-ci : une exonération
/// attachée à un article se voit à ce que l'article n'est jamais vendu
/// autrement ; une exonération attachée à un client se voit à ce que ce client
/// n'achète jamais taxé. Quand les deux se mêlent, aucune règle simple ne
/// suffira, et il vaut mieux le savoir avant de paramétrer.
/// </remarks>
public sealed record AuditTvaZero
{
    public IReadOnlyList<ArticleAZero> Articles { get; init; } = [];
    public IReadOnlyList<RegroupementAZero> Familles { get; init; } = [];
    public IReadOnlyList<RegroupementAZero> Clients { get; init; } = [];

    public int NombreFacturesConcernees { get; init; }
    public decimal MontantHTTotal { get; init; }

    /// <summary>Lignes de vente examinées, tous taux confondus.</summary>
    public int LignesExaminees { get; init; }

    public IReadOnlyList<ArticleAZero> ArticlesExclusivementAZero =>
        [.. Articles.Where(article => article.ExclusivementAZero)];

    public IReadOnlyList<ArticleAZero> ArticlesAPlusieursTaux =>
        [.. Articles.Where(article => !article.ExclusivementAZero)];

    public static AuditTvaZero Analyser(
        IReadOnlyCollection<SageDocumentHeader> entetes,
        IReadOnlyCollection<SageDocumentLine> lignes,
        IReadOnlyDictionary<string, SageCustomer> clients,
        IReadOnlyDictionary<string, string> familles,
        int clientsParArticle = 10,
        int exemplesParArticle = 5)
    {
        var tiersParPiece = entetes
            .GroupBy(entete => entete.Piece, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(groupe => groupe.Key, groupe => groupe.First().Tiers, StringComparer.OrdinalIgnoreCase);

        // Une ligne sans article ne se rattache à rien : on la compte, sans la
        // ranger sous une référence qui n'existe pas.
        var examinees = lignes.Where(ligne => !string.IsNullOrWhiteSpace(ligne.ArticleReference)).ToList();
        var aZero = examinees.Where(ligne => TaxMapping.TauxTva(ligne) == 0m).ToList();

        // Les autres taux du même article, pris sur tout le dossier lu.
        var tauxParArticle = examinees
            .GroupBy(ligne => ligne.ArticleReference, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                groupe => groupe.Key,
                groupe => groupe
                    .Select(TaxMapping.TauxTva)
                    .Where(taux => taux != 0m)
                    .Distinct()
                    .OrderBy(taux => taux)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

        var articles = aZero
            .GroupBy(ligne => ligne.ArticleReference, StringComparer.OrdinalIgnoreCase)
            .Select(groupe => Construire(
                groupe.Key, [.. groupe], tiersParPiece, clients, familles,
                tauxParArticle.GetValueOrDefault(groupe.Key, []),
                clientsParArticle, exemplesParArticle))
            .OrderByDescending(article => article.MontantHTCumule)
            .ThenBy(article => article.Reference, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AuditTvaZero
        {
            Articles = articles,
            Familles = Regrouper(
                examinees,
                ligne => familles.GetValueOrDefault(ligne.ArticleReference, ""),
                cle => cle == "" ? "— sans famille —" : cle),
            Clients = Regrouper(
                examinees,
                ligne => tiersParPiece.GetValueOrDefault(ligne.Piece, ""),
                compte => clients.TryGetValue(compte, out var client) ? client.Intitule : compte),
            NombreFacturesConcernees = aZero
                .Select(ligne => ligne.Piece)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            MontantHTTotal = aZero.Sum(ligne => ligne.MontantHT),
            LignesExaminees = examinees.Count,
        };
    }

    private static ArticleAZero Construire(
        string reference,
        IReadOnlyList<SageDocumentLine> lignes,
        IReadOnlyDictionary<string, string> tiersParPiece,
        IReadOnlyDictionary<string, SageCustomer> clients,
        IReadOnlyDictionary<string, string> familles,
        IReadOnlyList<decimal> autresTaux,
        int clientsParArticle,
        int exemplesParArticle)
    {
        // Les trois emplacements de taxe, tels qu'ils apparaissent réellement.
        // Rien ne garantit que la TVA soit toujours en position 1.
        var codes = lignes
            .SelectMany(ligne => ligne.Taxes().Select(taxe => (taxe.Emplacement, taxe.Code, taxe.Taux)))
            .Where(t => !string.IsNullOrWhiteSpace(t.Code) || t.Taux != 0m)
            .GroupBy(t => (t.Emplacement, Code: t.Code.Trim(), t.Taux))
            .Select(groupe => new CodeTaxeObserve(
                groupe.Key.Emplacement, groupe.Key.Code, groupe.Key.Taux, groupe.Count()))
            .OrderBy(code => code.Position).ThenByDescending(code => code.Lignes)
            .ToList();

        var parClient = lignes
            .GroupBy(ligne => tiersParPiece.GetValueOrDefault(ligne.Piece, ""), StringComparer.OrdinalIgnoreCase)
            .Select(groupe =>
            {
                clients.TryGetValue(groupe.Key, out var fiche);
                return new ClientAZero(
                    groupe.Key,
                    fiche?.Intitule ?? groupe.Key,
                    fiche?.Identifiant ?? "",
                    groupe.Count(),
                    groupe.Sum(ligne => ligne.MontantHT));
            })
            .OrderByDescending(client => client.MontantHT)
            .ToList();

        return new ArticleAZero
        {
            Reference = reference,
            Designation = lignes[0].Designation,
            Famille = familles.GetValueOrDefault(reference, ""),
            LignesAZero = lignes.Count,
            Factures = lignes.Select(ligne => ligne.Piece).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            QuantiteCumulee = lignes.Sum(ligne => ligne.Quantite),
            MontantHTCumule = lignes.Sum(ligne => ligne.MontantHT),
            CodesObserves = codes,
            LignesSansAucunCode = lignes.Count(ligne =>
                ligne.Taxes().All(taxe => string.IsNullOrWhiteSpace(taxe.Code) && taxe.Taux == 0m)),
            NombreClients = parClient.Count,
            Clients = [.. parClient.Take(clientsParArticle)],
            AutresTaux = autresTaux,
            ExemplesPieces = [.. lignes
                .Select(ligne => ligne.Piece)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(exemplesParArticle)],
        };
    }

    /// <summary>
    /// Combien de lignes à 0 % et combien de lignes taxées par regroupement.
    /// </summary>
    /// <remarks>
    /// C'est la comparaison qui informe, pas le compte isolé : une famille dont
    /// toutes les lignes sont à 0 % suggère une règle de famille ; une famille
    /// panachée dit que la règle est ailleurs.
    /// </remarks>
    private static List<RegroupementAZero> Regrouper(
        IReadOnlyCollection<SageDocumentLine> lignes,
        Func<SageDocumentLine, string> cle,
        Func<string, string> libelle) =>
        lignes
            .GroupBy(cle, StringComparer.OrdinalIgnoreCase)
            .Select(groupe => new RegroupementAZero(
                groupe.Key,
                libelle(groupe.Key),
                groupe.Count(ligne => TaxMapping.TauxTva(ligne) == 0m),
                groupe.Count(ligne => TaxMapping.TauxTva(ligne) != 0m),
                groupe.Where(ligne => TaxMapping.TauxTva(ligne) == 0m).Sum(ligne => ligne.MontantHT)))
            .Where(regroupement => regroupement.LignesAZero > 0)
            .OrderByDescending(regroupement => regroupement.MontantHTAZero)
            .ToList();
}

/// <param name="TauxTva">
/// Taux effectif de la ligne : la somme des emplacements de taxe qui ne sont
/// pas des prélèvements. Zéro n'y est pas un cas à part.
/// </param>
public sealed record OccurrenceArticle(
    string Piece,
    DateTime Date,
    string Compte,
    string Client,
    string Ncc,
    decimal Quantite,
    decimal MontantHT,
    decimal TauxTva,
    IReadOnlyList<CodeTaxeObserve> Codes);

/// <summary>
/// Toutes les apparitions d'un article dans le périmètre lu, taxées comprises.
/// </summary>
/// <remarks>
/// L'audit d'ensemble ne retient que les lignes à 0 %, ce qui suffit à
/// inventorier mais pas à trancher. La question « cet article est-il
/// exclusivement à 0 % ou panaché ? » demande de voir aussi les ventes taxées,
/// et de les compter.
///
/// Comme l'audit, ce relevé n'énonce que des faits : il ne dit ni
/// <c>TVAC</c> ni <c>TVAD</c>, et ne le peut pas.
/// </remarks>
public sealed record DetailArticle
{
    public required string Reference { get; init; }
    public string Designation { get; init; } = "";
    public string Famille { get; init; } = "";

    /// <summary>Chaque ligne où l'article apparaît, la plus récente d'abord.</summary>
    public IReadOnlyList<OccurrenceArticle> Occurrences { get; init; } = [];

    /// <summary>Combien de lignes à chaque taux rencontré, du plus fréquent au moins.</summary>
    public IReadOnlyList<(decimal Taux, int Lignes, decimal MontantHT)> ParTaux { get; init; } = [];

    public int NombreFactures { get; init; }
    public int NombreClients { get; init; }

    /// <summary>Vrai quand aucune vente de cet article n'est taxée.</summary>
    public bool ExclusivementAZero => ParTaux.All(t => t.Taux == 0m);

    /// <summary>Vrai quand l'article est vendu tantôt à 0 %, tantôt taxé.</summary>
    public bool Panache => ParTaux.Any(t => t.Taux == 0m) && ParTaux.Any(t => t.Taux != 0m);

    public static DetailArticle? Construire(
        string reference,
        IReadOnlyCollection<SageDocumentHeader> entetes,
        IReadOnlyCollection<SageDocumentLine> lignes,
        IReadOnlyDictionary<string, SageCustomer> clients,
        IReadOnlyDictionary<string, string> familles)
    {
        var concernees = lignes
            .Where(ligne => string.Equals(ligne.ArticleReference, reference, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (concernees.Count == 0) return null;

        var parPiece = entetes
            .GroupBy(entete => entete.Piece, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(groupe => groupe.Key, groupe => groupe.First(), StringComparer.OrdinalIgnoreCase);

        var occurrences = concernees
            .Select(ligne =>
            {
                parPiece.TryGetValue(ligne.Piece, out var entete);
                var compte = entete?.Tiers ?? "";
                clients.TryGetValue(compte, out var fiche);

                return new OccurrenceArticle(
                    ligne.Piece,
                    entete?.Date ?? default,
                    compte,
                    fiche?.Intitule ?? compte,
                    fiche?.Identifiant ?? "",
                    ligne.Quantite,
                    ligne.MontantHT,
                    TaxMapping.TauxTva(ligne),
                    [.. ligne.Taxes()
                        .Where(taxe => !string.IsNullOrWhiteSpace(taxe.Code) || taxe.Taux != 0m)
                        .Select(taxe => new CodeTaxeObserve(taxe.Emplacement, taxe.Code.Trim(), taxe.Taux, 1))]);
            })
            .OrderByDescending(occurrence => occurrence.Date)
            .ThenBy(occurrence => occurrence.Piece, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DetailArticle
        {
            Reference = reference,
            Designation = concernees[0].Designation,
            Famille = familles.GetValueOrDefault(reference, ""),
            Occurrences = occurrences,
            ParTaux = [.. occurrences
                .GroupBy(occurrence => occurrence.TauxTva)
                .Select(groupe => (groupe.Key, groupe.Count(), groupe.Sum(o => o.MontantHT)))
                .OrderByDescending(t => t.Item2)
                .ThenBy(t => t.Key)],
            NombreFactures = occurrences.Select(o => o.Piece).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            NombreClients = occurrences.Select(o => o.Compte).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
        };
    }
}
