namespace SageFne.Core.Models.Sage;

/// <summary>
/// Les domaines de <c>F_DOCENTETE</c> que le middleware sait traiter.
/// </summary>
/// <remarks>
/// Sage numérote ses domaines, et le dossier réel a confirmé ce que
/// <c>domaines</c> montrait : le 0 porte les ventes — types 3, 4, 6 et 7, sur
/// des comptes <c>411…</c> — et le 1 porte les achats — types 16 et 17, sur des
/// comptes <c>401…</c>. Le 2, sans montants, n'est pas de notre ressort.
///
/// Ces valeurs ne sont pas devinées : elles ont été relevées. Un dossier qui
/// numéroterait autrement se verrait dans <c>domaines</c> avant qu'une seule
/// facture ne parte.
/// </remarks>
public static class SageDomaines
{
    public const short Vente = 0;
    public const short Achat = 1;

    public static string Libelle(short domaine) => domaine switch
    {
        Vente => "vente",
        Achat => "achat",
        _ => $"domaine {domaine}",
    };
}

/// <summary>
/// Les types de documents d'achat, relevés sur le dossier réel.
/// </summary>
/// <remarks>
/// 16 et 17, comme 6 et 7 pour les ventes : deux états d'un même document, que
/// la comptabilisation fait passer de l'un à l'autre. Le lot lit les deux, et
/// l'identité d'une pièce s'appuie sur <c>DO_DocType</c>, qui ne bouge pas.
///
/// Le dossier HT en porte 318 du premier et 7 du second — ce que <c>domaines</c>
/// a montré avant qu'une ligne de code d'achat ne soit écrite.
/// </remarks>
public static class SagePurchaseTypes
{
    public const short Facture = 16;
    public const short FactureComptabilisee = 17;

    public static readonly short[] Factures = [Facture, FactureComptabilisee];
}
