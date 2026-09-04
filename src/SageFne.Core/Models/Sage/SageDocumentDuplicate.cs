namespace SageFne.Core.Models.Sage;

/// <summary>
/// Un numéro de pièce porté par plusieurs documents de types différents.
/// </summary>
/// <remarks>
/// Deux lectures possibles, opposées :
///
/// <list type="bullet">
/// <item>Types {6, 7} — une facture et sa version comptabilisée coexistent.
/// C'est le cas dangereux : le même document serait certifié deux fois.</item>
/// <item>Types {3, 6} par exemple — un bon de livraison et une facture qui
/// partagent un numéro de souche. Sans gravité : le lot ne lit pas les bons de
/// livraison, et l'identité du registre inclut le type d'origine.</item>
/// </list>
/// </remarks>
public sealed class SageDocumentDuplicate
{
    public required string Piece { get; init; }
    public required IReadOnlyList<short> Types { get; init; }
    public required IReadOnlyList<short> DocTypes { get; init; }
    public int Nombre { get; init; }

    /// <summary>Le cas qui empêcherait d'envoyer : facture et comptabilisée à la fois.</summary>
    public bool MemeFacture =>
        Types.Contains(SageDocumentTypes.Facture)
        && Types.Contains(SageDocumentTypes.FactureComptabilisee);
}
