namespace SageFne.Reader.Configuration;

/// <summary>Valeurs propres au dossier, à renseigner dans appsettings.json.</summary>
public sealed class FneOptions
{
    public const string Section = "Fne";

    public string PointOfSale { get; set; } = "";
    public string Establishment { get; set; } = "";
    /// <summary>Mode de règlement par défaut, tant que Sage ne le fournit pas.</summary>
    public string PaymentMethod { get; set; } = "deferred";
    public string Template { get; set; } = "B2B";

    /// <summary>
    /// Régime appliqué aux lignes à 0 % de TVA, faute de règle plus précise.
    /// </summary>
    /// <remarks>
    /// Valeurs acceptées : <c>Unknown</c>, <c>ConventionalExemption</c>,
    /// <c>LegalExemptionTeeRme</c>. <b>Unknown par défaut, et c'est voulu</b> :
    /// TVAC et TVAD valent tous deux 0 % et Sage ne les distingue pas. Une
    /// facture dont le régime reste inconnu est bloquée plutôt que d'annoncer à
    /// la DGI une exonération qu'on aurait devinée.
    /// </remarks>
    public string ZeroVatCategory { get; set; } = "Unknown";

    /// <summary>
    /// Régime par référence d'article (AR_Ref), quand l'exonération tient au
    /// produit — un bien légalement exonéré.
    /// </summary>
    public Dictionary<string, string> ZeroVatCategoryByArticle { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Régime par compte client (CT_Num), quand l'exonération tient au client —
    /// une entreprise titulaire d'un régime d'exonération.
    /// </summary>
    public Dictionary<string, string> ZeroVatCategoryByCustomer { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fichier du registre des certifications. Vide : « certifications.json »
    /// à côté de l'exécutable. Ce registre ne peut pas vivre dans Sage, dont
    /// l'accès est en lecture seule.
    /// </summary>
    public string CertificationLedgerPath { get; set; } = "";
}
