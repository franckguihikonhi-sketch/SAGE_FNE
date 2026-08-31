using SageFne.Reader.Models.Fne;
using SageFne.Reader.Models.Sage;
using SageFne.Reader.Validation;

namespace SageFne.Reader.Mapping;

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
