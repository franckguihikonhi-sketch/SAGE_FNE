namespace SageFne.Core.Mapping;

/// <summary>
/// Les types de facturation que la DGI propose, et ce qu'ils désignent.
/// </summary>
/// <remarks>
/// Relevés sur le formulaire « Modifier la facture » du portail FNE, où ils
/// forment une liste fermée de quatre valeurs. Le middleware envoyait jusqu'ici
/// une chaîne libre lue dans <c>appsettings.json</c> : une faute de frappe —
/// <c>B2B </c> avec une espace, <c>BTB</c>, <c>b2b</c> — serait partie telle
/// quelle et aurait été certifiée telle quelle, ou refusée sans qu'on sache
/// pourquoi.
///
/// Les libellés viennent du portail ; les valeurs envoyées à l'API sont
/// reprises telles que le paramétrage les porte, en majuscules. Si l'API
/// attendait autre chose, c'est ici que cela se corrigera — en un seul endroit.
/// </remarks>
public static class GabaritFne
{
    public const string Entreprise = "B2B";
    public const string ConsommateurFinal = "B2C";
    public const string International = "B2F";
    public const string EtatEtCollectivites = "B2G";

    private static readonly Dictionary<string, string> Libelles = new(StringComparer.OrdinalIgnoreCase)
    {
        [Entreprise] = "entreprise",
        [ConsommateurFinal] = "consommateur final",
        [International] = "client international",
        [EtatEtCollectivites] = "État et collectivités",
    };

    public static IReadOnlyCollection<string> Connus => Libelles.Keys;

    public static bool Reconnu(string? gabarit) =>
        gabarit is not null && Libelles.ContainsKey(gabarit.Trim());

    /// <summary>Le libellé du portail, ou la valeur telle quelle si elle est inconnue.</summary>
    public static string Libelle(string? gabarit) =>
        gabarit is not null && Libelles.TryGetValue(gabarit.Trim(), out var libelle)
            ? libelle
            : "inconnu";

    /// <summary>
    /// Vrai quand ce gabarit exige le NCC du client.
    /// </summary>
    /// <remarks>
    /// Seul <c>B2B</c> est établi : le portail marque le NCC obligatoire quand
    /// il est sélectionné, et un consommateur final n'a pas de numéro
    /// contribuable à donner. Ce que <c>B2F</c> et <c>B2G</c> exigent n'a pas
    /// été vérifié — un client international n'a pas de NCC ivoirien, une
    /// administration en a probablement un. Tant que ce n'est pas tranché avec
    /// la DGI, ces deux-là ne sont pas traités comme exigeant le NCC, et le
    /// paramétrage qui les emploie doit le savoir.
    /// </remarks>
    public static bool ExigeNcc(string? gabarit) =>
        Entreprise.Equals(gabarit?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Ce qu'il faut écrire, pour un message d'erreur.</summary>
    public static string Attendus =>
        string.Join(", ", Libelles.Select(entree => $"{entree.Key} ({entree.Value})"));
}
