using SageFne.Core.Models.Fne;

namespace SageFne.Core.Fne;

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
public interface IFneApiClient
{
    /// <summary>Vrai quand l'implémentation part réellement sur le réseau.</summary>
    bool Reel { get; }

    /// <summary>La requête qui serait envoyée, pour la montrer avant de l'envoyer.</summary>
    string DecrireRequete(FneInvoice facture);

    Task<FneSignResult> SignAsync(FneInvoice facture, CancellationToken cancellation = default);
}

/// <summary>
/// L'annulation d'une facture certifiée, par un avoir.
/// </summary>
/// <remarks>
/// Interface distincte de <see cref="IFneApiClient"/>, et non une méthode de
/// plus sur celle-ci : seize doublures de test l'implémentent, et aucune n'a
/// affaire aux avoirs. Ajouter la méthode là-bas les aurait toutes cassées, ou
/// — pire — forcé une implémentation par défaut qui aurait fait croire à un
/// avoir là où rien ne serait parti.
///
/// L'avoir est d'ailleurs une capacité distincte : le service ne l'a pas et ne
/// doit pas l'avoir. Annuler une facture certifiée est une décision, jamais un
/// automatisme.
/// </remarks>
public interface IFneAvoirClient
{
    /// <summary>La requête qui partirait, pour la montrer avant de l'envoyer.</summary>
    string DecrireAvoir(string idFacture, CorpsAvoir corps);

    Task<FneSignResult> RembourserAsync(
        string idFacture, CorpsAvoir corps, CancellationToken cancellation = default);
}
