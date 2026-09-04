using System.Text.Json.Serialization;

namespace SageFne.Core.Models.Fne;

/// <summary>
/// Facture au format attendu par la plateforme FNE de la DGI.
/// </summary>
/// <remarks>
/// L'ordre des propriétés est celui du corps de requête documenté : le JSON
/// produit se relit à côté de la documentation sans chercher.
/// </remarks>
public sealed class FneInvoice
{
    [JsonPropertyName("invoiceType")]
    public string InvoiceType { get; init; } = "sale";

    [JsonPropertyName("paymentMethod")]
    public string PaymentMethod { get; init; } = "deferred";

    [JsonPropertyName("template")]
    public string Template { get; init; } = "B2B";

    [JsonPropertyName("isRne")]
    public bool IsRne { get; init; }

    [JsonPropertyName("clientNcc")]
    public string ClientNcc { get; init; } = "";

    [JsonPropertyName("clientCompanyName")]
    public string ClientCompanyName { get; init; } = "";

    [JsonPropertyName("clientPhone")]
    public string ClientPhone { get; init; } = "";

    [JsonPropertyName("clientEmail")]
    public string ClientEmail { get; init; } = "";

    [JsonPropertyName("clientSellerName")]
    public string ClientSellerName { get; init; } = "";

    [JsonPropertyName("pointOfSale")]
    public string PointOfSale { get; init; } = "";

    [JsonPropertyName("establishment")]
    public string Establishment { get; init; } = "";

    [JsonPropertyName("commercialMessage")]
    public string CommercialMessage { get; init; } = "";

    [JsonPropertyName("footer")]
    public string Footer { get; init; } = "";

    [JsonPropertyName("items")]
    public IReadOnlyList<FneInvoiceItem> Items { get; init; } = [];

    [JsonPropertyName("discount")]
    public decimal Discount { get; init; }
}
