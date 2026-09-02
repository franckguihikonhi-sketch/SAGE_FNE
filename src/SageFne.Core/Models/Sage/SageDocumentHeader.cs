namespace SageFne.Core.Models.Sage;

/// <summary>
/// Entête d'un document des ventes, lu dans F_DOCENTETE.
/// </summary>
/// <remarks>
/// DO_TotalHT vaut 0 sur une partie des documents du dossier : il est repris
/// ici pour information, mais le HT de référence se recalcule depuis les
/// lignes. Voir <see cref="Validation.FinancialChecks"/>.
/// </remarks>
public sealed class SageDocumentHeader
{
    public required short Domaine { get; init; }
    public required short Type { get; init; }
    public required string Piece { get; init; }
    public required DateTime Date { get; init; }
    /// <summary>Compte tiers du document (DO_Tiers), clé vers F_COMPTET.</summary>
    public required string Tiers { get; init; }
    /// <summary>
    /// DO_DocType : le type d'<b>origine</b> du document, que la comptabilisation
    /// ne modifie pas. Une facture porte 6, que DO_Type vaille 6 ou 7.
    /// </summary>
    public short DocType { get; init; }

    public decimal TotalHT { get; init; }
    public decimal TotalTTC { get; init; }
    public decimal NetAPayer { get; init; }
    public short Statut { get; init; }

    /// <summary>
    /// Le type qui identifie le document, insensible à la comptabilisation.
    /// </summary>
    /// <remarks>
    /// DO_DocType fait foi. Il vaut 0 sur les dossiers où la colonne n'est pas
    /// alimentée : dans ce cas seulement, DO_Type prend le relais — un document
    /// déjà filtré comme facture ne peut pas être un devis.
    /// </remarks>
    public short TypeOrigine => DocType != 0 ? DocType : Type;

    /// <summary>
    /// Identité stable d'un document, du brouillon à la comptabilisation.
    /// </summary>
    /// <remarks>
    /// DO_Piece seul ne suffit pas : Sage numérote par souche, et un bon de
    /// livraison peut porter le même numéro qu'une facture. DO_Type ne convient
    /// pas non plus, puisqu'il passe de 6 à 7. Le couple type d'origine + numéro
    /// est le seul qui désigne la même facture avant et après comptabilisation.
    /// </remarks>
    public string Identite => $"{Domaine}/{TypeOrigine}/{Piece}";

    public bool EstComptabilisee => Type == SageDocumentTypes.FactureComptabilisee;
}
