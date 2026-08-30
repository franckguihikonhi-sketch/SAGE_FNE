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
    /// Code appliqué aux lignes sans TVA : TVAD pour l'exonération légale,
    /// TVAC pour l'exonération conventionnelle.
    /// </summary>
    public string ExemptionCode { get; set; } = "TVAD";
}
