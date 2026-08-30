namespace SageFne.Reader.Models.Sage;

/// <summary>
/// Entête d'un document des ventes, lu dans F_DOCENTETE.
/// </summary>
/// <remarks>
/// DO_TotalHT vaut 0 sur une partie des documents du dossier : il est repris
/// ici pour information, mais le HT de référence se recalcule depuis les
/// lignes. Voir <see cref="Validation.FinancialChecks"/>.
/// </remarks>
public sealed class SageDocumentHeader
{
    public required short Domaine { get; init; }
    public required short Type { get; init; }
    public required string Piece { get; init; }
    public required DateTime Date { get; init; }
    /// <summary>Compte tiers du document (DO_Tiers), clé vers F_COMPTET.</summary>
    public required string Tiers { get; init; }
    public decimal TotalHT { get; init; }
    public decimal TotalTTC { get; init; }
    public decimal NetAPayer { get; init; }
    public short Statut { get; init; }
}
