namespace SageFne.Reader.Models.Sage;

/// <summary>
/// Les types de documents du domaine des ventes que ce dossier utilise.
/// </summary>
/// <remarks>
/// Relevés sur le dossier HT par <c>doctypes</c> : 3 bons de livraison,
/// 4 bons de retour, 6 factures, 7 factures comptabilisées.
///
/// <b>6 et 7 sont deux états d'un même document</b>, pas deux documents. Quand
/// une facture est comptabilisée, Sage fait passer DO_Type de 6 à 7 sur la
/// ligne existante et laisse <c>DO_DocType</c> à 6 — la trace du type d'origine.
/// C'est ce qui a été constaté : les documents de type 7 du dossier portent
/// tous DO_DocType = 6.
///
/// D'où la règle : le lot lit <b>6 et 7</b>, et l'identité d'une facture ne
/// s'appuie jamais sur DO_Type, qui bouge, mais sur DO_DocType et DO_Piece,
/// qui ne bougent pas.
/// </remarks>
public static class SageDocumentTypes
{
    public const short Devis = 0;
    public const short BonCommande = 1;
    public const short BonLivraison = 3;
    public const short BonRetour = 4;
    public const short Facture = 6;
    public const short FactureComptabilisee = 7;

    /// <summary>Les deux états d'une facture. Le lot lit les deux.</summary>
    public static readonly short[] Factures = [Facture, FactureComptabilisee];

    /// <summary>
    /// Ce qui ne part jamais automatiquement à la DGI.
    /// </summary>
    /// <remarks>
    /// Un bon de livraison n'est pas une facture. Un bon de retour appelle un
    /// avoir, dont le traitement reste à écrire : le certifier comme une vente
    /// facturerait au client ce qu'il vient de rendre.
    /// </remarks>
    public static readonly short[] Exclus = [Devis, BonCommande, BonLivraison, BonRetour];

    public static bool EstFacture(short type) => Factures.Contains(type);

    public static string Libelle(short type) => type switch
    {
        Devis => "Devis",
        BonCommande => "Bon de commande",
        2 => "Préparation de livraison",
        BonLivraison => "Bon de livraison",
        BonRetour => "Bon de retour",
        5 => "Bon d'avoir financier",
        Facture => "Facture",
        FactureComptabilisee => "Facture comptabilisée",
        _ => "",
    };

    /// <summary>Pourquoi un type est écarté, en clair.</summary>
    public static string RaisonExclusion(short type) => type switch
    {
        BonLivraison => "un bon de livraison n'est pas une facture : rien n'y est dû.",
        BonRetour =>
            "un bon de retour appelle un avoir, pas une facture. Le certifier comme une " +
            "vente facturerait au client ce qu'il vient de rendre.",
        Devis or BonCommande or 2 or 5 =>
            $"{Libelle(type).ToLowerInvariant()} : document qui ne fonde aucune créance.",
        _ => $"type {type} hors du périmètre des factures.",
    };
}
