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
            var taxes = TaxMapping.Read(ligne, _options.ExemptionCode);
            foreach (var avertissement in taxes.Avertissements)
            {
                report?.Avertir("TAUX_HORS_NOMENCLATURE", avertissement);
            }

            if (taxes.Taxes.Count == 0)
            {
                // Sans TVA reconnue et sans exonération applicable : la ligne
                // porte un taux que la nomenclature ne connaît pas.
                report?.Avertir(
                    "LIGNE_SANS_CODE_TAXE",
                    $"ligne {ligne.Ligne} : aucun code de taxe FNE n'a pu être établi.");
            }

            var remise = RemiseMapping.Read(ligne);
            foreach (var avertissement in remise.Avertissements)
            {
                report?.Avertir("REMISE_NON_CONCORDANTE", $"ligne {ligne.Ligne} : {avertissement}");
            }

            items.Add(new FneInvoiceItem
            {
                Taxes = taxes.Taxes,
                CustomTaxes = taxes.CustomTaxes,
                Reference = ligne.ArticleReference,
                Description = ligne.Designation,
                Quantity = ligne.Quantite,
                // FNE attend le prix unitaire HT, pas le montant de la ligne, et
                // il le multiplie par la quantité. La remise est donc déduite du
                // prix plutôt que déclarée à part : le total certifié est celui
                // que le client a payé, sans dépendre de ce que FNE entend par
                // « discount » — un point à confirmer sur la documentation DGI.
                Amount = remise.PrixUnitaireNet,
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
