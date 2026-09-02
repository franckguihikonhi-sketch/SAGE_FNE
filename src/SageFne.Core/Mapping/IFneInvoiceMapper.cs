using SageFne.Core.Models.Fne;
using SageFne.Core.Models.Sage;
using SageFne.Core.Validation;

namespace SageFne.Core.Mapping;

public interface IFneInvoiceMapper
{
    /// <summary>Traduit une pièce Sage en facture FNE, sans rien inventer.</summary>
    FneInvoice Map(
        SageDocumentHeader header,
        IReadOnlyCollection<SageDocumentLine> lines,
        SageCustomer customer,
        CheckReport? report = null,
        IReadOnlyDictionary<string, string>? famillesParArticle = null,
        TaxCatalogue? catalogue = null);
}
