using System.Text.Json.Serialization;

namespace SageFne.Core.Models.Fne;

/// <summary>
/// Ligne d'article de la facture FNE.
/// </summary>
/// <remarks>
/// <c>amount</c> est le prix unitaire HT, pas le total de la ligne.
/// <c>taxes</c> ne porte que la TVA (TVA, TVAB) ; tout prélèvement d'une autre
/// nature, l'AIRSI en particulier, passe par <c>customTaxes</c>.
/// </remarks>
public sealed class FneInvoiceItem
{
    /// <summary>
    /// Les codes de TVA. <c>null</c> sur un bordereau d'achat, où le champ
    /// n'est pas envoyé du tout.
    /// </summary>
    /// <remarks>
    /// Le tableau des paramètres du bordereau d'achat, dans la procédure de la
    /// DGI, ne mentionne ni <c>taxes</c> ni <c>customTaxes</c> — contrairement à
    /// celui de la vente. Un achat à un producteur ne porte pas de TVA.
    ///
    /// Omis plutôt qu'envoyé vide : nous ne savons pas ce que la plateforme
    /// fait d'un <c>"taxes": []</c> sur un achat, et un tableau vide est une
    /// affirmation — « il n'y a aucune taxe » — là où l'absence de champ n'en
    /// est pas une. La vente, elle, continue d'envoyer la liste telle quelle,
    /// vide comprise : son chemin ne change pas.
    /// </remarks>
    [JsonPropertyName("taxes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Taxes { get; init; } = [];

    [JsonPropertyName("customTaxes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<FneCustomTax>? CustomTaxes { get; init; } = [];

    [JsonPropertyName("reference")]
    public string Reference { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; init; }

    [JsonPropertyName("amount")]
    public decimal Amount { get; init; }

    [JsonPropertyName("discount")]
    public decimal Discount { get; init; }

    [JsonPropertyName("measurementUnit")]
    public string MeasurementUnit { get; init; } = "";
}
