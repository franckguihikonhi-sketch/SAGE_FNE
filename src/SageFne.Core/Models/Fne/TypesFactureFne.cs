namespace SageFne.Core.Models.Fne;

/// <summary>
/// Les deux valeurs d'<c>invoiceType</c> que nous envoyons, et la question
/// « est-ce un achat ? » posée en un seul endroit.
/// </summary>
/// <remarks>
/// Ce discriminant décide de la forme du corps de requête, pas seulement de son
/// étiquette : le bordereau d'achat de l'API n° 3 n'a ni <c>taxes</c>, ni
/// <c>customTaxes</c>, ni <c>clientNcc</c>. Tout code qui lit un
/// <see cref="FneInvoiceItem"/> doit donc pouvoir savoir de quel côté il se
/// trouve, sinon il déréférence une liste absente.
///
/// D'où cette classe plutôt qu'un littéral répété : le mapper l'écrivait, la
/// complétude et l'aperçu l'ignoraient, et les deux plantaient sur un achat.
/// </remarks>
public static class TypesFactureFne
{
    public const string Vente = "sale";
    public const string Achat = "purchase";

    public static bool EstAchat(FneInvoice facture) =>
        string.Equals(facture.InvoiceType, Achat, StringComparison.OrdinalIgnoreCase);
}
