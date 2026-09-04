namespace SageFne.Core.Saas;

/// <summary>Ce qu'une publication a donné.</summary>
/// <param name="Publiees">Lignes acceptées par la base.</param>
/// <param name="Refusees">
/// Lignes que la base a refusées. Ce n'est pas une panne : c'est une garantie
/// SQL qui s'applique — une transition impossible, une référence qui change sur
/// une pièce certifiée. Le registre local porte alors quelque chose que la base
/// tient pour faux, et il faut le savoir.
/// </param>
/// <param name="Empechement">Ce qui a empêché toute publication, le cas échéant.</param>
public sealed record ResultatPublication(
    int Publiees,
    int Refusees = 0,
    string? Empechement = null,
    string Detail = "")
{
    public static readonly ResultatPublication Inactif = new(0, 0, "miroir non configuré");
    public bool Aboutie => Empechement is null;
}

/// <summary>
/// La publication du registre vers la base d'audit.
/// </summary>
/// <remarks>
/// Interface distincte du reste, et sans aucun lien avec l'envoi à la DGI :
/// <b>rien de ce qui se passe ici ne peut modifier une certification</b>. Le
/// miroir lit le registre et l'écrit ailleurs ; il ne décide de rien.
/// </remarks>
public interface IMiroirClient
{
    bool Actif { get; }

    Task<ResultatPublication> PublierAsync(
        IReadOnlyList<LigneMiroir> lignes, CancellationToken cancellation = default);
}
