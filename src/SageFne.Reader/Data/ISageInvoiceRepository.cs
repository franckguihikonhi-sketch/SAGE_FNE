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

    /// <summary>Paramétrage des taxes du dossier, pour information.</summary>
    Task<List<SageTaxDefinition>> GetTaxesAsync(CancellationToken cancellation = default);
}
