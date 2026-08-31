using SageFne.Reader.Models.Fne;

namespace SageFne.Reader.Fne;

/// <summary>
/// Ce que la plateforme a répondu.
/// </summary>
/// <param name="Reussi">La facture est certifiée.</param>
/// <param name="CodeHttp">Code de statut, quand la requête a abouti.</param>
/// <param name="ReferenceFne">Référence certifiée, si elle a pu être lue.</param>
/// <param name="Token">Jeton de vérification (QR code), si présent.</param>
/// <param name="CorpsBrut">
/// La réponse telle quelle. Le format exact n'étant pas connu d'avance, le brut
/// est conservé : c'est lui qui permettra de corriger la lecture des champs.
/// </param>
/// <param name="Erreur">Ce qui a échoué, en clair.</param>
public sealed record FneSignResult(
    bool Reussi,
    int? CodeHttp = null,
    string? ReferenceFne = null,
    string? Token = null,
    string CorpsBrut = "",
    string? Erreur = null);

/// <summary>Envoi d'une facture à la certification.</summary>
public interface IFneClient
{
    /// <summary>Vrai quand l'implémentation part réellement sur le réseau.</summary>
    bool Reel { get; }

    /// <summary>La requête qui serait envoyée, pour la montrer avant de l'envoyer.</summary>
    string DecrireRequete(FneInvoice facture);

    Task<FneSignResult> SignAsync(FneInvoice facture, CancellationToken cancellation = default);
}
