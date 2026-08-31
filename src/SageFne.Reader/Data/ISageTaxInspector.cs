using SageFne.Reader.Models.Sage;

namespace SageFne.Reader.Data;

/// <summary>
/// Lectures d'exploration, pour chercher une information dont on ignore où
/// elle se trouve.
/// </summary>
/// <remarks>
/// Séparé de <see cref="ISageInvoiceRepository"/> parce que le besoin est
/// différent : le dépôt lit ce que le mapping utilise, avec des modèles typés.
/// Ici on cherche — quelles colonnes de F_TAXE, F_COMPTET ou F_ARTICLE
/// pourraient distinguer une exonération conventionnelle d'une exonération
/// légale — et cela demande de tout voir, sans rien supposer.
///
/// Strictement en lecture, comme le reste.
/// </remarks>
public interface ISageTaxInspector
{
    /// <summary>Toutes les lignes d'une table, toutes colonnes.</summary>
    Task<List<SageEnregistrement>> LireTableAsync(
        string table,
        int limite = 200,
        CancellationToken cancellation = default);

    /// <summary>Une ligne désignée par une de ses colonnes.</summary>
    Task<SageEnregistrement?> LireLigneAsync(
        string table,
        string colonneCle,
        string valeur,
        CancellationToken cancellation = default);

    /// <summary>
    /// Les colonnes fiscales brutes des lignes d'une pièce, telles quelles.
    /// </summary>
    Task<List<SageEnregistrement>> LireFiscaliteLignesAsync(
        string piece,
        CancellationToken cancellation = default);
}
