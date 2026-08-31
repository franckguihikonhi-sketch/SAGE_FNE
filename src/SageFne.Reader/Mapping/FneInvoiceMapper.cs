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
    private readonly IZeroVatPolicy _regimes = new ConfiguredZeroVatPolicy(options.Value.ZeroVat);

    /// <param name="famillesParArticle">
    /// FA_CodeFamille par AR_Ref, quand la classification par famille doit
    /// pouvoir jouer. F_DOCLIGNE ne porte pas la famille : elle vient d'une
    /// lecture de F_ARTICLE faite par le lot.
    /// </param>
    /// <param name="catalogue">Ce que le dossier dit de ses taxes.</param>
    public FneInvoice Map(
        SageDocumentHeader header,
        IReadOnlyCollection<SageDocumentLine> lines,
        SageCustomer customer,
        CheckReport? report = null,
        IReadOnlyDictionary<string, string>? famillesParArticle = null,
        TaxCatalogue? catalogue = null)
    {
        var items = new List<FneInvoiceItem>(lines.Count);

        foreach (var ligne in lines.OrderBy(ligne => ligne.Ligne))
        {
            var famille = famillesParArticle is not null
                && famillesParArticle.TryGetValue(ligne.ArticleReference, out var trouvee)
                ? trouvee
                : "";

            var decision = _regimes.Decider(
                new ZeroVatContexte(ligne.ArticleReference, famille, customer.CtNum));

            var taxes = TaxMapping.Read(ligne, decision.Regime, catalogue);

            // Une règle mal écrite ne se contourne pas en passant au niveau
            // suivant : elle se signale.
            if (decision.Erreur is not null)
            {
                report?.Erreur("ZERO_VAT_CATEGORY_INVALID", $"ligne {ligne.Ligne} : {decision.Erreur}");
            }

            foreach (var prelevement in taxes.PrelevementsSansMapping)
            {
                report?.Erreur(TaxMapping.CodePrelevementSansMapping, prelevement);
            }

            // Une TVA à 0 % dont le régime n'est pas classé bloque la pièce :
            // TVAC et TVAD valent tous deux 0 %, et annoncer à la DGI une
            // exonération devinée ne se corrige plus qu'avec un avoir.
            if (taxes.RegimeZeroRequis)
            {
                foreach (var avertissement in taxes.Avertissements)
                {
                    report?.Erreur(
                        TaxMapping.CodeRegimeInconnu,
                        $"{avertissement} Règle consultée : {decision.Origine}.");
                }
            }
            else
            {
                foreach (var avertissement in taxes.Avertissements)
                {
                    report?.Avertir("TAUX_HORS_NOMENCLATURE", avertissement);
                }
            }

            if (taxes.Taxes.Count == 0 && !taxes.RegimeZeroRequis)
            {
                // Ni TVA reconnue, ni exonération : la ligne porte un taux que
                // la nomenclature ne connaît pas.
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

        // Sage ne porte pas le mode de règlement du document. La valeur vient du
        // paramétrage, et cela doit se voir sur chaque pièce plutôt que dans une
        // note de bas de page.
        report?.Avertir(
            "PAYMENT_METHOD_SUPPOSE",
            $"paymentMethod = « {_options.PaymentMethod} », valeur du paramétrage : " +
            "Sage ne porte pas le mode de règlement de la pièce. À confirmer avec la DGI.");

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
