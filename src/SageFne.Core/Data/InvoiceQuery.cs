namespace SageFne.Core.Data;

/// <summary>
/// Ce qu'on demande à lire : des pièces nommées, ou une période.
/// </summary>
/// <remarks>
/// Les deux critères peuvent se combiner. <see cref="Jusqua"/> est exclusive :
/// une période du 1er au 31 décembre se demande avec Jusqua = 1er janvier, ce
/// qui évite de perdre les documents datés en fin de journée.
/// </remarks>
public sealed record InvoiceQuery
{
    /// <summary>Numéros de pièce précis. Vide : la période décide seule.</summary>
    public IReadOnlyList<string> Pieces { get; init; } = [];

    public DateTime? Depuis { get; init; }

    /// <summary>Borne haute, exclue.</summary>
    public DateTime? Jusqua { get; init; }

    /// <summary>Garde-fou : un lot trop large sature la console et la mémoire.</summary>
    public int Limite { get; init; } = 500;

    public static InvoiceQuery Piece(string piece) => new() { Pieces = [piece] };

    public bool EstVide => Pieces.Count == 0 && Depuis is null && Jusqua is null;

    public string Describe()
    {
        var morceaux = new List<string>();
        if (Pieces.Count > 0) morceaux.Add($"pièce(s) {string.Join(", ", Pieces)}");
        if (Depuis is not null) morceaux.Add($"du {Depuis:dd/MM/yyyy}");
        if (Jusqua is not null) morceaux.Add($"avant le {Jusqua:dd/MM/yyyy}");
        return morceaux.Count == 0 ? "tout le domaine des ventes" : string.Join(", ", morceaux);
    }
}
