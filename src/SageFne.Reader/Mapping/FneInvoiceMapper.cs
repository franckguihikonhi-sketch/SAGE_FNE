using Microsoft.Extensions.Options;
using SageFne.Reader.Configuration;
using SageFne.Reader.Models.Fne;
using SageFne.Reader.Models.Sage;
using SageFne.Reader.Validation;

namespace SageFne.Reader.Mapping;

/// <summary>
/// De la pièce Sage à la facture FNE.
/// </summary>
/// <remarks>
/// Ce qui manque encore dans Sage — le mode de règlement, le point de vente,
/// l'établissement — vient du paramétrage plutôt que d'être deviné.
/// </remarks>
public sealed class FneInvoiceMapper(IOptions<FneOptions> options) : IFneInvoiceMapper
{
    private readonly FneOptions _options = options.Value;

    public FneInvoice Map(
        SageDocumentHeader header,
        IReadOnlyCollection<SageDocumentLine> lines,
        SageCustomer customer,
        CheckReport? report = null)
    {
        var items = new List<FneInvoiceItem>(lines.Count);

        foreach (var ligne in lines.OrderBy(ligne => ligne.Ligne))
        {
            var taxes = TaxMapping.Read(ligne);
            foreach (var avertissement in taxes.Avertissements)
            {
                report?.Avertir("TAUX_HORS_NOMENCLATURE", avertissement);
            }

            if (taxes.Taxes.Count == 0)
            {
                // FNE distingue l'exonération conventionnelle (TVAC) de
                // l'exonération légale (TVAD). Tant que le dossier ne dit pas
                // laquelle s'applique, la ligne part sans code de TVA.
                report?.Avertir(
                    "LIGNE_SANS_TVA",
                    $"ligne {ligne.Ligne} : aucun taux de TVA sur la ligne, aucun code n'est ajouté. " +
                    "À confirmer avec la DGI si l'exonération doit porter TVAC ou TVAD.");
            }

            items.Add(new FneInvoiceItem
            {
                Taxes = taxes.Taxes,
                CustomTaxes = taxes.CustomTaxes,
                Reference = ligne.ArticleReference,
                Description = ligne.Designation,
                Quantity = ligne.Quantite,
                // FNE attend le prix unitaire HT, pas le montant de la ligne.
                Amount = ligne.PrixUnitaire,
                Discount = 0m,
                MeasurementUnit = ligne.Unite,
            });
        }

        return new FneInvoice
        {
            InvoiceType = "sale",
            PaymentMethod = _options.PaymentMethod,
            Template = _options.Template,
            IsRne = false,
            ClientNcc = customer.Identifiant,
            ClientCompanyName = customer.Intitule,
            ClientPhone = customer.Telephone,
            ClientEmail = customer.Email,
            ClientSellerName = "",
            PointOfSale = _options.PointOfSale,
            Establishment = _options.Establishment,
            CommercialMessage = "",
            Footer = "",
            Items = items,
            Discount = 0m,
        };
    }
}
