namespace SageFne.Core.Certification;

/// <summary>
/// L'état du fichier de registre, tel qu'un diagnostic peut le décrire.
/// </summary>
/// <param name="Chemin">Chemin absolu, résolu — celui qu'il faut sauvegarder.</param>
/// <param name="Illisible">Renseigné quand le fichier existe mais ne se lit pas.</param>
public sealed record RegistreFichier(
    string Chemin,
    bool Existe,
    long Octets = 0,
    DateTime? ModifieLe = null,
    IReadOnlyList<CertifiedInvoice>? Entrees = null,
    string? Illisible = null);
