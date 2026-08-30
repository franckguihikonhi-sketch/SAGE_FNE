using System.Text.Json.Serialization;

namespace SageFne.Reader.Certification;

/// <summary>
/// Trace d'une pièce déjà certifiée par la DGI.
/// </summary>
/// <remarks>
/// Cette trace ne peut pas vivre dans Sage : l'accès y est en lecture seule,
/// et rien n'y prévoit de zone pour la référence FNE. Elle vit donc dans un
/// registre à nous, à côté de l'application.
///
/// L'empreinte est celle du corps de requête envoyé à la DGI. Elle permet de
/// distinguer deux situations que rien d'autre ne sépare : une pièce déjà
/// certifiée et inchangée, qu'il faut ignorer, et une pièce certifiée puis
/// modifiée dans Sage, qu'il faut signaler — la facture certifiée ne
/// correspond plus à ce que le dossier contient.
/// </remarks>
public sealed record CertifiedInvoice
{
    [JsonPropertyName("piece")]
    public required string Piece { get; init; }

    /// <summary>Référence certifiée renvoyée par la plateforme.</summary>
    [JsonPropertyName("referenceFne")]
    public string ReferenceFne { get; init; } = "";

    /// <summary>Jeton de vérification (QR code).</summary>
    [JsonPropertyName("token")]
    public string Token { get; init; } = "";

    [JsonPropertyName("certifieeLe")]
    public DateTimeOffset CertifieeLe { get; init; }

    /// <summary>Empreinte du corps envoyé, pour repérer une pièce modifiée depuis.</summary>
    [JsonPropertyName("empreinte")]
    public string Empreinte { get; init; } = "";
}
