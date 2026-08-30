using System.Text.Json.Serialization;

namespace SageFne.Reader.Models.Fne;

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
    [JsonPropertyName("taxes")]
    public IReadOnlyList<string> Taxes { get; init; } = [];

    [JsonPropertyName("customTaxes")]
    public IReadOnlyList<FneCustomTax> CustomTaxes { get; init; } = [];

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
