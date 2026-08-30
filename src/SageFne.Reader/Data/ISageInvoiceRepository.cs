using SageFne.Reader.Models.Sage;

namespace SageFne.Reader.Data;

/// <summary>
/// Lecture des documents de vente du dossier Sage. Aucune écriture.
/// </summary>
public interface ISageInvoiceRepository
{
    /// <summary>Entête d'une pièce de vente (DO_Domaine = 0).</summary>
    Task<SageDocumentHeader?> GetInvoiceAsync(string piece, CancellationToken cancellation = default);

    /// <summary>Lignes de la pièce, dans l'ordre de DL_Ligne.</summary>
    Task<List<SageDocumentLine>> GetInvoiceLinesAsync(string piece, CancellationToken cancellation = default);

    /// <summary>Fiche du client, par son compte tiers.</summary>
    Task<SageCustomer?> GetCustomerAsync(string ctNum, CancellationToken cancellation = default);

    /// <summary>Entêtes répondant au critère, dans l'ordre des dates puis des pièces.</summary>
    Task<List<SageDocumentHeader>> GetInvoicesAsync(InvoiceQuery query, CancellationToken cancellation = default);

    /// <summary>
    /// Toutes les lignes du lot, en une seule lecture.
    /// </summary>
    /// <remarks>
    /// Lire les lignes facture par facture ferait un aller-retour par pièce :
    /// sur un mois de facturation, c'est la différence entre une seconde et
    /// une minute. Le regroupement se fait ensuite en mémoire.
    /// </remarks>
    Task<List<SageDocumentLine>> GetLinesAsync(InvoiceQuery query, CancellationToken cancellation = default);

    /// <summary>Fiches clients demandées, en une seule lecture.</summary>
    Task<List<SageCustomer>> GetCustomersAsync(
        IReadOnlyCollection<string> ctNums,
        CancellationToken cancellation = default);

    /// <summary>Paramétrage des taxes du dossier, pour information.</summary>
    Task<List<SageTaxDefinition>> GetTaxesAsync(CancellationToken cancellation = default);
}
