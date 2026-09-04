namespace SageFne.Core.Models.Sage;

/// <summary>
/// Ce qu'un couple domaine / type de document porte réellement.
/// </summary>
/// <remarks>
/// Aucun domaine n'est nommé ici, et c'est délibéré. Sage numérote ses domaines
/// — 0, 1, 2… — et l'usage veut que 0 soit la vente et 1 l'achat, mais ce
/// « l'usage veut » est exactement le genre de supposition qui a déjà coûté
/// cher à ce projet. Le dossier dit ce qu'il contient ; c'est l'exploitant qui
/// reconnaît ses documents.
/// </remarks>
public sealed class SageDomaineSummary
{
    public required short Domaine { get; init; }
    public required short Type { get; init; }
    public required int Nombre { get; init; }
    public DateTime? PremiereDate { get; init; }
    public DateTime? DerniereDate { get; init; }
    public decimal TotalTTC { get; init; }

    /// <summary>Un exemplaire, pour juger sur pièce.</summary>
    public string Exemple { get; init; } = "";

    /// <summary>Le compte tiers de cet exemplaire.</summary>
    public string Tiers { get; init; } = "";
}
