namespace SageFne.Reader.Certification;

/// <summary>
/// D'où vient ce que le registre affirme d'une pièce.
/// </summary>
/// <remarks>
/// Cette distinction n'est pas cosmétique. Une référence lue dans la réponse
/// de la DGI est un fait ; une référence recopiée depuis un portail est une
/// déclaration humaine, qui peut être fautive — elle l'a été une fois, avec
/// une valeur d'exemple inscrite telle quelle. Un audit doit pouvoir les
/// séparer sans lire les commentaires.
/// </remarks>
public enum SourceCertification
{
    /// <summary>Le middleware a envoyé la facture et lu la réponse de la DGI.</summary>
    Middleware,

    /// <summary>
    /// Un humain a constaté la certification sur le portail et l'a inscrite.
    /// </summary>
    /// <remarks>
    /// L'empreinte est alors celle du document au moment du rattrapage, et non
    /// celle du corps réellement envoyé, qui est perdu avec la trace.
    /// </remarks>
    ReconciliationManuelle,

    /// <summary>Reprise d'un registre antérieur ou d'un autre outil.</summary>
    Import,
}
