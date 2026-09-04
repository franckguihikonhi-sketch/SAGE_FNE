namespace SageFne.Core.Models.Sage;

/// <summary>Une colonne et sa valeur, telles que la base les rend.</summary>
/// <param name="Colonne">Nom exact de la colonne dans le dossier.</param>
/// <param name="Valeur">Valeur convertie en texte, vide si NULL.</param>
public readonly record struct SageChamp(string Colonne, string Valeur)
{
    /// <summary>
    /// Une colonne vide, à zéro, ou portant la date nulle de Sage ne dit rien :
    /// le diagnostic les met de côté pour laisser voir celles qui parlent.
    /// </summary>
    /// <remarks>
    /// Sage écrit 01/01/1753 — la borne basse de son type date — plutôt que
    /// NULL. Une soixantaine de colonnes d'une fiche client la portent.
    /// </remarks>
    public bool Renseigne
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Valeur)) return false;
            if (Valeur.StartsWith("01/01/1753", StringComparison.Ordinal)) return false;

            var nombre = Valeur.Replace(',', '.');
            return !decimal.TryParse(
                       nombre,
                       System.Globalization.NumberStyles.Any,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out var valeur)
                   || valeur != 0m;
        }
    }

    /// <summary>
    /// Les colonnes « cb… » sont les champs binaires de réplication de Sage :
    /// jamais fonctionnelles, et elles noient le reste.
    /// </summary>
    public bool Interne => Colonne.StartsWith("cb", StringComparison.Ordinal);
}

/// <summary>
/// Une ligne de table lue sans a priori, colonne par colonne.
/// </summary>
/// <remarks>
/// Les modèles typés du projet — <see cref="SageDocumentLine"/> et les autres —
/// ne lisent que ce que le mapping utilise. Pour <b>chercher</b> une
/// information dont on ignore le nom de colonne, il faut au contraire tout
/// voir : c'est le rôle de ce type, réservé aux commandes de diagnostic.
/// </remarks>
public sealed class SageEnregistrement
{
    public required string Table { get; init; }

    /// <summary>Ce qui identifie la ligne, pour l'affichage.</summary>
    public required string Cle { get; init; }

    public required IReadOnlyList<SageChamp> Champs { get; init; }

    /// <summary>Ce qui porte une information, les champs internes en dernier.</summary>
    public IEnumerable<SageChamp> Renseignes => Champs
        .Where(champ => champ.Renseigne)
        .OrderBy(champ => champ.Interne)
        .ThenBy(champ => champ.Colonne, StringComparer.OrdinalIgnoreCase);

    public string? Valeur(string colonne) => Champs
        .Where(champ => string.Equals(champ.Colonne, colonne, StringComparison.OrdinalIgnoreCase))
        .Select(champ => champ.Valeur)
        .FirstOrDefault();

    /// <summary>
    /// Les colonnes dont le <b>nom</b> évoque la fiscalité.
    /// </summary>
    /// <remarks>
    /// Un filtre sur le nom, pas sur le sens : il sert à porter le regard, pas
    /// à conclure. C'est au lecteur de dire si « CT_Classement » désigne un
    /// régime d'exonération dans ce dossier.
    /// </remarks>
    public IEnumerable<SageChamp> Fiscaux => Champs
        .Where(champ => !champ.Interne)
        .Where(champ => Indices.Any(indice =>
            champ.Colonne.Contains(indice, StringComparison.OrdinalIgnoreCase)))
        .OrderBy(champ => champ.Colonne, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] Indices =
    [
        "taxe", "tva", "tax", "exo", "fiscal", "nif", "identifiant",
        "categ", "famille", "classement", "regroup", "edi", "compta", "cg_num", "assujetti",
    ];
}
