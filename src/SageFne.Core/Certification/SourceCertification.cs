namespace SageFne.Core.Certification;

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
    /// <summary>
    /// Rien ne dit d'où vient cette ligne.
    /// </summary>
    /// <remarks>
    /// Première valeur de l'énumération, donc celle que prend le champ quand il
    /// est absent du JSON — les entrées écrites avant l'ajout de <c>source</c>
    /// sont dans ce cas.
    ///
    /// Ce n'était pas ainsi au départ : <see cref="Middleware"/> occupait la
    /// place, si bien qu'une entrée sans <c>source</c> se relisait « la DGI l'a
    /// dit », c'est-à-dire l'affirmation la plus forte que le champ sache
    /// porter. Une réconciliation manuelle réelle s'est ainsi retrouvée classée
    /// réponse de plateforme, et devenue incorrigible — les corrections étant
    /// justement réservées aux déclarations humaines.
    ///
    /// La valeur par défaut d'une énumération ne doit jamais être une
    /// affirmation. Elle est ici l'aveu d'une ignorance, ce qu'elle est
    /// réellement.
    /// </remarks>
    Inconnue,

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
