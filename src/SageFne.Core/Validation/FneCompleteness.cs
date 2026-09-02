using SageFne.Core.Mapping;
using SageFne.Core.Models.Fne;

namespace SageFne.Core.Validation;

/// <summary>
/// Ce que la facture traduite ne porte pas encore, et que la DGI attend.
/// </summary>
/// <remarks>
/// Les contrôles de <see cref="InvoiceValidator"/> regardent la pièce Sage.
/// Celui-ci regarde le corps de requête lui-même, une fois construit : c'est le
/// dernier endroit où un champ vide se voit avant qu'il ne parte.
///
/// Un gabarit non remplacé — « A_COMPLETER » dans appsettings.json — compte
/// comme un champ vide. Il partirait tel quel, et serait certifié tel quel.
/// </remarks>
public static class FneCompleteness
{
    private static readonly string[] Gabarits = ["A_COMPLETER", "A_RENSEIGNER", "TODO", "XXX"];

    public sealed record Manque(string Champ, string Origine, string Consequence);

    /// <summary>
    /// Les deux champs d'identité du contribuable qui ne sont pas encore
    /// renseignés, décrits en clair. Vide quand tout va bien.
    /// </summary>
    /// <remarks>
    /// Ces deux-là seulement, et à dessein. Le reste de <see cref="Verifier"/>
    /// porte sur la pièce, et les contrôles de pièce le couvrent déjà — le
    /// dupliquer dans l'expéditeur ferait deux règles pour un même fait, avec
    /// la divergence au bout.
    ///
    /// <c>pointOfSale</c> et <c>establishment</c>, eux, ne viennent pas de Sage
    /// mais du paramétrage : aucun contrôle de pièce ne peut les voir. Une
    /// facture irréprochable part donc avec « A_COMPLETER » et se fait refuser
    /// par la DGI — « Establishment is invalid » — sans que rien n'ait pu le
    /// prévoir. C'est arrivé, sur quatre pièces d'affilée.
    /// </remarks>
    public static IReadOnlyList<string> IdentiteAControler(FneInvoice facture)
    {
        var manques = new List<string>();

        if (Absent(facture.PointOfSale))
        {
            manques.Add($"le point de vente n'est pas renseigné (« {facture.PointOfSale} »)");
        }

        if (Absent(facture.Establishment))
        {
            manques.Add($"l'établissement n'est pas renseigné (« {facture.Establishment} »)");
        }

        return manques;
    }

    public static List<Manque> Verifier(FneInvoice facture, string template)
    {
        var manques = new List<Manque>();

        void Exiger(string champ, string valeur, string origine, string consequence)
        {
            if (Absent(valeur)) manques.Add(new Manque(champ, origine, consequence));
        }

        Exiger("pointOfSale", facture.PointOfSale, "appsettings.json, section Fne",
            "point de vente déclaré à la DGI : la facture serait rattachée à un point inconnu.");
        Exiger("establishment", facture.Establishment, "appsettings.json, section Fne",
            "établissement déclaré à la DGI.");
        Exiger("clientCompanyName", facture.ClientCompanyName, "CT_Intitule",
            "nom du client sur la facture certifiée.");

        // Étoilé sur le formulaire de la DGI, quel que soit le type de
        // facturation. clientEmail ne l'est pas — la 1052 est partie sans, et a
        // été certifiée : ce qui vaut pour l'un ne vaut pas pour l'autre.
        Exiger("clientPhone", facture.ClientPhone, "CT_Telephone",
            "téléphone du client : obligatoire sur le formulaire de la DGI.");

        if (GabaritFne.ExigeNcc(template))
        {
            Exiger("clientNcc", facture.ClientNcc, "CT_Identifiant",
                "identifiant fiscal du client : obligatoire en B2B, la certification serait refusée.");
        }

        for (var rang = 0; rang < facture.Items.Count; rang++)
        {
            var item = facture.Items[rang];
            var ou = $"items[{rang}]";

            Exiger($"{ou}.description", item.Description, "DL_Design", "libellé de la ligne.");

            if (item.Taxes.Count == 0)
            {
                manques.Add(new Manque($"{ou}.taxes", "DL_Taxe1/2/3 et DL_CodeTaxe1/2/3",
                    "code de taxe FNE : aucune ligne ne peut partir sans."));
            }

            if (item.Quantity <= 0m)
            {
                manques.Add(new Manque($"{ou}.quantity", "DL_Qte",
                    $"quantité de {item.Quantity}, attendue strictement positive."));
            }
        }

        return manques;
    }

    /// <summary>Ce qui est fourni par le paramétrage faute de source dans Sage.</summary>
    public static List<Manque> Hypotheses(FneInvoice facture) =>
    [
        new("paymentMethod", $"figé à « {facture.PaymentMethod} » dans appsettings.json",
            "Sage ne porte pas le mode de règlement du document : à confirmer avec la DGI " +
            "si un autre mode s'applique."),
        new("invoiceType", $"figé à « {facture.InvoiceType} »",
            "toutes les pièces partent comme des ventes. Les avoirs (DO_Type 4) ne sont pas traités."),
        new("clientSellerName", "non renseigné",
            "nom du vendeur : Sage ne le porte pas sur le document."),
        new("isRne", $"déclaré à « {(facture.IsRne ? "true" : "false")} » dans appsettings.json",
            "régime de l'entreprise émettrice — le vôtre, pas celui du client. " +
            "Sage ne le porte pas : vérifiez qu'il correspond à votre situation."),
    ];

    private static bool Absent(string valeur) =>
        string.IsNullOrWhiteSpace(valeur)
        || Gabarits.Any(gabarit => valeur.Trim().Equals(gabarit, StringComparison.OrdinalIgnoreCase));
}
