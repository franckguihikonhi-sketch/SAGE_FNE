namespace SageFne.Core.Certification;

/// <summary>
/// Le registre existe mais n'a pas pu être lu.
/// </summary>
/// <remarks>
/// Cette exception remplace un comportement qui était dangereux : un registre
/// illisible était traité comme vide, signalé dans les journaux et rien de plus.
/// Or un registre vide signifie « aucune pièce n'a jamais été certifiée ». Un
/// fichier tronqué faisait donc réapparaître comme envoyables toutes les
/// factures déjà certifiées par la DGI — et un doublon ne se rattrape pas.
///
/// Mieux vaut tout arrêter et le dire. Un registre qu'on ne sait pas lire n'est
/// pas un registre vide : c'est un registre inconnu.
/// </remarks>
public sealed class RegistreIllisibleException(string chemin, Exception cause)
    : Exception(
        $"Le registre des certifications est illisible : {chemin}. " +
        "Tant qu'il ne l'est pas, aucune pièce ne peut être jugée : un registre " +
        "qu'on ne sait pas lire n'est pas un registre vide, et traiter l'un pour " +
        "l'autre ferait repartir des factures déjà certifiées. Restaurez-le depuis " +
        "une sauvegarde, ou corrigez le fichier à la main.",
        cause)
{
    public string Chemin { get; } = chemin;
}
