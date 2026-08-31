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
    /// Classification des lignes à 0 % de TVA. Voir <see cref="ZeroVatOptions"/>.
    /// </summary>
    public ZeroVatOptions ZeroVat { get; set; } = new();

    /// <summary>
    /// Prélèvements Sage repris en <c>customTaxes</c>, par leur code.
    /// </summary>
    /// <remarks>
    /// Un mapping <b>explicite</b>, code Sage vers nom FNE. Reprendre
    /// automatiquement tout ce qui n'est pas une TVA ferait partir à la DGI des
    /// prélèvements sous un nom que personne n'a validé. Un code absent d'ici
    /// est signalé, jamais deviné.
    /// </remarks>
    public Dictionary<string, string> CustomTaxes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase) { ["AIRSI"] = "AIRSI" };

    /// <summary>
    /// Fichier du registre des certifications. Vide : « certifications.json »
    /// à côté de l'exécutable. Ce registre ne peut pas vivre dans Sage, dont
    /// l'accès est en lecture seule.
    /// </summary>
    public string CertificationLedgerPath { get; set; } = "";
}
