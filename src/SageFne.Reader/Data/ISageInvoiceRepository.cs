using SageFne.Reader.Models.Sage;

namespace SageFne.Reader.Data;

/// <summary>
/// Lecture des documents de vente du dossier Sage. Aucune écriture.
/// </summary>
public interface ISageInvoiceRepository
{
    /// <summary>Entête d'une pièce de vente (DO_Domaine = 0).</summary>
    Task<SageDocumentHeader?> GetInvoiceAsync(string piece, CancellationToken cancellation = default);

    /// <summary>Lignes de la pièce, dans l'ordre de DL_Ligne.</summary>
    Task<List<SageDocumentLine>> GetInvoiceLinesAsync(string piece, CancellationToken cancellation = default);

    /// <summary>Fiche du client, par son compte tiers.</summary>
    Task<SageCustomer?> GetCustomerAsync(string ctNum, CancellationToken cancellation = default);

    /// <summary>Entêtes répondant au critère, dans l'ordre des dates puis des pièces.</summary>
    Task<List<SageDocumentHeader>> GetInvoicesAsync(InvoiceQuery query, CancellationToken cancellation = default);

    /// <summary>
    /// Toutes les lignes du lot, en une seule lecture.
    /// </summary>
    /// <remarks>
    /// Lire les lignes facture par facture ferait un aller-retour par pièce :
    /// sur un mois de facturation, c'est la différence entre une seconde et
    /// une minute. Le regroupement se fait ensuite en mémoire.
    /// </remarks>
    Task<List<SageDocumentLine>> GetLinesAsync(InvoiceQuery query, CancellationToken cancellation = default);

    /// <summary>Fiches clients demandées, en une seule lecture.</summary>
    Task<List<SageCustomer>> GetCustomersAsync(
        IReadOnlyCollection<string> ctNums,
        CancellationToken cancellation = default);

    /// <summary>Paramétrage des taxes du dossier, pour information.</summary>
    Task<List<SageTaxDefinition>> GetTaxesAsync(CancellationToken cancellation = default);

    /// <summary>
    /// Inventaire des types de documents présents dans le domaine des ventes.
    /// </summary>
    /// <remarks>
    /// Diagnostic : quels DO_Type ce dossier utilise, combien de documents
    /// chacun porte, et quelques exemplaires pour juger sur pièce.
    /// </remarks>
    Task<List<SageDocumentTypeSummary>> GetDocumentTypesAsync(
        int exemplesParType = 5,
        CancellationToken cancellation = default);

    /// <summary>
    /// Tous les documents portant ce numéro de pièce, quel que soit leur type.
    /// </summary>
    /// <remarks>
    /// Diagnostic : le lot ne lit que les factures, donc demander une pièce qui
    /// se trouve être un bon de livraison ne renvoie rien, sans dire pourquoi.
    /// Cette lecture-là voit tout et permet de l'expliquer.
    /// </remarks>
    Task<List<SageDocumentHeader>> GetDocumentsByPieceAsync(
        string piece,
        CancellationToken cancellation = default);

    /// <summary>
    /// Numéros de pièce présents sous plusieurs types à la fois.
    /// </summary>
    /// <remarks>
    /// La question décisive sur la relation 6 → 7 : si la comptabilisation
    /// modifie la ligne existante, aucun numéro ne porte les deux types et le
    /// risque de double certification n'existe pas. S'il en sort, il faut le
    /// savoir avant d'envoyer quoi que ce soit.
    /// </remarks>
    Task<List<SageDocumentDuplicate>> GetPiecesMultiTypesAsync(
        CancellationToken cancellation = default);

    /// <summary>
    /// Ce que les tables du dossier ne portent pas, parmi ce que la lecture
    /// attend.
    /// </summary>
    /// <remarks>
    /// Les colonnes de Sage varient d'une version à l'autre : le dossier HT n'a
    /// pas de DL_DocType. Plutôt que de le découvrir par une exception au milieu
    /// d'un lot, on le demande au catalogue.
    /// </remarks>
    Task<List<SageColonnesManquantes>> GetColonnesManquantesAsync(
        CancellationToken cancellation = default);
}
