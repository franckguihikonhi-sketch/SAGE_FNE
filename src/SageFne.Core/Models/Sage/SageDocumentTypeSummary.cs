namespace SageFne.Core.Models.Sage;

/// <summary>
/// Un exemplaire de document, pour donner à voir ce que contient un type.
/// </summary>
public sealed class SageDocumentSample
{
    public required string Piece { get; init; }
    public DateTime Date { get; init; }
    public string Tiers { get; init; } = "";
    public decimal TotalTTC { get; init; }
    /// <summary>DO_DocType, quand la colonne existe dans ce dossier.</summary>
    public short? DocType { get; init; }
}

/// <summary>
/// Ce que le dossier contient pour un DO_Type donné.
/// </summary>
/// <remarks>
/// Diagnostic pur : rien ici ne décide de ce qui part à la DGI. Il sert à
/// répondre à une question qu'on ne peut pas trancher depuis l'extérieur —
/// quels types de documents ce dossier utilise réellement, et lesquels sont
/// des factures à certifier.
/// </remarks>
public sealed class SageDocumentTypeSummary
{
    public required short Type { get; init; }
    public required int Nombre { get; init; }
    public DateTime? PremiereDate { get; init; }
    public DateTime? DerniereDate { get; init; }
    public decimal TotalTTC { get; init; }
    public IReadOnlyList<SageDocumentSample> Exemples { get; init; } = [];

    /// <summary>
    /// Libellé usuel du type dans Sage 100. Indicatif : le dossier peut avoir
    /// son propre paramétrage, et c'est la colonne DO_Type qui fait foi.
    /// </summary>
    public string LibelleUsuel => Type switch
    {
        0 => "Devis",
        1 => "Bon de commande",
        2 => "Préparation de livraison",
        3 => "Bon de livraison",
        4 => "Bon de retour",
        5 => "Bon d'avoir financier",
        6 => "Facture",
        7 => "Facture comptabilisée",
        _ => "",
    };
}
