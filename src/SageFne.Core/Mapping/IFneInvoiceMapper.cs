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
        TaxCatalogue? catalogue = null,

        /// <summary>
        /// Le mode de règlement retenu pour cette pièce, s'il l'a été.
        /// </summary>
        /// <remarks>
        /// Par appel, et non par constructeur : le mapping est un singleton, si
        /// bien qu'un dictionnaire posé à sa construction resterait figé — et
        /// dans le câblage de production, vide pour toujours. Le mode change
        /// pendant que le service tourne, à chaque choix de l'exploitant.
        /// </remarks>
        string? modePaiement = null);
}
