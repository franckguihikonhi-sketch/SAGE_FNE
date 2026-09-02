using Microsoft.Extensions.Options;
using SageFne.Core.Configuration;
using SageFne.Core.Fne;
using SageFne.Core.Models.Fne;
using SageFne.Core.Models.Sage;
using SageFne.Core.Validation;

namespace SageFne.Core.Mapping;

/// <summary>
/// De la pièce Sage à la facture FNE.
/// </summary>
/// <remarks>
/// Ce qui manque encore dans Sage — le mode de règlement, le point de vente,
/// l'établissement — vient du paramétrage plutôt que d'être deviné.
/// </remarks>
public sealed class FneInvoiceMapper(IOptions<FneOptions> options, IZeroVatPolicy? regimes = null)
    : IFneInvoiceMapper
{
    private readonly FneOptions _options = options.Value;
    /// <remarks>
    /// Injectée quand le registre des règles la fournit ; sinon, la politique
    /// adossée au seul paramétrage, qui suffit aux tests et au jeu d'essai.
    /// </remarks>
    private readonly IZeroVatPolicy _regimes = regimes ?? new ConfiguredZeroVatPolicy(options.Value.ZeroVat);


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
        TaxCatalogue? catalogue = null,
        string? modePaiement = null)
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

            var taxes = TaxMapping.Read(ligne, decision.Code, catalogue);

            // Une règle mal écrite ne se contourne pas en passant au niveau
            // suivant : elle se signale.
            if (decision.Erreur is not null)
            {
                report?.Erreur("ZERO_VAT_CATEGORY_INVALID", $"ligne {ligne.Ligne} : {decision.Erreur}");
            }

            // Une règle acceptée mais qui affirme un fondement à la place d'un
            // code : elle décide, et se signale.
            if (decision.Avertissement is not null)
            {
                report?.Avertir("ZERO_VAT_VALEUR_HERITEE", $"ligne {ligne.Ligne} : {decision.Avertissement}");
            }

            // Quelle règle a décidé, pour que la facture certifiée puisse le
            // dire. Sans cela, modifier une règle rendrait indéchiffrables les
            // factures parties sous la précédente.
            if (taxes.Taxes.Contains("TVAC") || taxes.Taxes.Contains("TVAD"))
            {
                report?.Avertir(
                    "ZERO_VAT_REGLE",
                    $"ligne {ligne.Ligne} : {decision.Code.Libelle()} par « {decision.Origine} » " +
                    $"— fondement {decision.Fondement.Libelle()}.");
            }

            foreach (var prelevement in taxes.PrelevementsSansMapping)
            {
                report?.Erreur(TaxMapping.CodePrelevementSansMapping, prelevement);
            }

            // Une TVA à 0 % dont le régime n'est pas classé bloque la pièce :
            // TVAC et TVAD valent tous deux 0 %, et annoncer à la DGI une
            // exonération devinée ne se corrige plus qu'avec un avoir.
            if (taxes.TvaAbsente)
            {
                // Bloquant comme une TVA à 0 % non classée, mais sous un autre
                // code et pour une autre raison : ici, rien n'a été déclaré.
                // La règle consultée n'est pas mentionnée — il n'y a pas de
                // règle à chercher, et en nommer une orienterait vers le
                // mauvais remède.
                foreach (var avertissement in taxes.Avertissements)
                {
                    report?.Erreur(TaxMapping.CodeTvaAbsente, avertissement);
                }
            }
            else if (taxes.RegimeZeroRequis)
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

            if (taxes.Taxes.Count == 0 && !taxes.RegimeZeroRequis && !taxes.TvaAbsente)
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
        // Le corps envoyé ne porte aucune date : la plateforme n'a pas de champ
        // pour celle de Sage, et la facture sera donc datée du jour du dépôt.
        // Tant que l'émission et le dépôt tombent le même jour, cela ne se voit
        // pas. Un agent arrêté un week-end, une correction reprise le
        // surlendemain, et la DGI certifie une facture sous une date qui n'est
        // pas celle du document Sage - un écart fiscal, pas un détail
        // d'affichage.
        //
        // Rien n'est inventé pour autant : aucun nom de champ n'est supposé,
        // aucune date n'est ajoutée au corps. L'écart est signalé quand il
        // existe, et la question est posée à la DGI.
        var jours = (DateTime.Today - header.Date.Date).Days;
        if (jours != 0)
        {
            report?.Avertir(
                "DATE_EMISSION_NON_TRANSMISE",
                $"La pièce est datée du {header.Date:dd/MM/yyyy} et serait déposée " +
                $"aujourd'hui, {DateTime.Today:dd/MM/yyyy} — {Math.Abs(jours)} jour(s) " +
                "d'écart. Le corps FNE ne porte aucun champ de date : la facture serait " +
                "certifiée à la date du dépôt, pas à celle du document Sage.");
        }

        // Le mode retenu pour ce client l'emporte sur le paramétrage. Sage ne
        // porte pas le mode de règlement dans les colonnes que nous lisons : il
        // est choisi à l'écran, facture par facture, avant de certifier.
        var modeRetenu = ModePaiementFne.Normaliser(modePaiement);

        var modeEnvoye = modeRetenu ?? _options.PaymentMethod;

        if (modeRetenu is null)
        {
            // On retombe sur le paramétrage. Deux gravités, parce que les deux
            // cas ne coûtent pas la même chose : une valeur plausible mais non
            // choisie est un avertissement ; une valeur que la DGI n'accepte
            // pas est une erreur, puisqu'elle fera refuser la facture.
            if (ModePaiementFne.EstConnu(_options.PaymentMethod))
            {
                // Le libellé ET le code : le premier se lit, le second est ce
                // qui part réellement à la DGI. N'afficher que le libellé
                // cacherait la valeur transmise.
                report?.Avertir(
                    "PAYMENT_METHOD_SUPPOSE",
                    $"paymentMethod = « {ModePaiementFne.Libelle(_options.PaymentMethod)} » " +
                    $"({_options.PaymentMethod}), valeur du paramétrage : personne n'a choisi " +
                    "le mode de règlement de cette pièce, et Sage ne porte pas cette " +
                    "information. La facture serait certifiée sur une supposition.");
            }
            else
            {
                report?.Erreur(
                    "PAYMENT_METHOD_INCONNU",
                    $"Fne:PaymentMethod vaut « {_options.PaymentMethod} », qui n'est aucun des " +
                    "six modes de la DGI (cash, card, check, mobile-money, transfer, deferred). " +
                    "La plateforme refuserait la facture.");
            }
        }

        return new FneInvoice
        {
            InvoiceType = "sale",
            PaymentMethod = modeEnvoye,
            Template = _options.Template,
            IsRne = _options.IsRne,
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
