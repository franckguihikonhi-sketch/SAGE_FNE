using System.Text.Json.Serialization;

namespace SageFne.Core.Models.Fne;

/// <summary>
/// Un article de l'avoir : ce qu'on rend, et combien.
/// </summary>
/// <remarks>
/// L'<c>id</c> n'est pas celui de Sage. C'est l'identifiant que la DGI a
/// attribué à la ligne au moment de la certification, et qu'elle a renvoyé dans
/// <c>invoice.items[].id</c>. Rien d'autre ne la désigne de son côté.
/// </remarks>
public sealed record ArticleAvoir(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("quantity")] decimal Quantity);

/// <summary>
/// Le corps de <c>POST /external/invoices/{id}/refund</c>.
/// </summary>
/// <remarks>
/// Le tableau des paramètres de la procédure DGI ne porte que <c>items</c>, et
/// chaque article que son <c>id</c> et sa <c>quantity</c>, tous deux
/// obligatoires. Rien de plus n'est envoyé : ni montant, ni motif, ni référence
/// — la facture d'origine est désignée par l'identifiant dans l'URL.
/// </remarks>
public sealed record CorpsAvoir(
    [property: JsonPropertyName("items")] IReadOnlyList<ArticleAvoir> Items);
