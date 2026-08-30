using System.Text.Json.Serialization;

namespace SageFne.Reader.Models.Fne;

/// <summary>
/// Prélèvement autre que la TVA — l'AIRSI dans ce dossier.
/// </summary>
/// <param name="Name">Nom du prélèvement, tel que Sage le code.</param>
/// <param name="Amount">Son taux, en pourcentage.</param>
public sealed record FneCustomTax(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("amount")] decimal Amount);
