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
    /// <summary>
    /// Clé du registre : domaine / type d'origine / numéro de pièce.
    /// </summary>
    /// <remarks>
    /// Le numéro seul ne peut pas servir de clé. Sage fait passer DO_Type de 6
    /// à 7 quand la facture est comptabilisée : c'est le même document, et il
    /// doit rester reconnu comme certifié. À l'inverse, un bon de livraison peut
    /// porter le même numéro qu'une facture sans être le même document.
    /// DO_DocType et DO_Piece, eux, ne bougent ni l'un ni l'autre.
    /// </remarks>
    [JsonPropertyName("identite")]
    public required string Identite { get; init; }

    /// <summary>Numéro de pièce, pour la lisibilité du registre.</summary>
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

    /// <summary>
    /// Où en est la pièce dans la chaîne de certification.
    /// </summary>
    /// <remarks>
    /// Cet état vit uniquement ici. La base Sage est en lecture seule et ne
    /// porte aucune zone pour lui.
    /// </remarks>
    [JsonPropertyName("etat")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Fne.EtatFne Etat { get; init; } = Fne.EtatFne.Certified;

    /// <summary>La réponse de la plateforme, telle quelle.</summary>
    /// <remarks>
    /// Conservée pour qu'un envoi dont l'issue est douteuse puisse être
    /// instruit après coup, sans dépendre de ce que le code a su en lire.
    /// </remarks>
    [JsonPropertyName("reponse")]
    public string Reponse { get; init; } = "";

    /// <summary>Ce qui a échoué, quand l'état est <c>Error</c> ou <c>Sending</c>.</summary>
    [JsonPropertyName("erreur")]
    public string Erreur { get; init; } = "";
}
