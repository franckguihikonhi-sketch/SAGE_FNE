namespace SageFne.Core.Models.Sage;

/// <summary>
/// Ce qu'une table du dossier ne porte pas, parmi ce que la lecture attend.
/// </summary>
/// <remarks>
/// Le dossier HT n'a pas de <c>DL_DocType</c> dans F_DOCLIGNE, et la lecture
/// entière échouait sur ce seul nom. Ce relevé rend l'écart visible avant qu'il
/// ne devienne une exception.
/// </remarks>
public sealed class SageColonnesManquantes
{
    public required string Table { get; init; }

    /// <summary>Nombre total de colonnes de la table dans ce dossier.</summary>
    public int Total { get; init; }

    /// <summary>Nombre de colonnes que la lecture aimerait avoir.</summary>
    public int Demandees { get; init; }

    public IReadOnlyList<string> Absentes { get; init; } = [];

    /// <summary>Celles sans lesquelles rien ne peut être lu.</summary>
    public IReadOnlyList<string> AbsentesIndispensables { get; init; } = [];

    public bool Complet => Absentes.Count == 0;
    public bool Utilisable => AbsentesIndispensables.Count == 0;
}
