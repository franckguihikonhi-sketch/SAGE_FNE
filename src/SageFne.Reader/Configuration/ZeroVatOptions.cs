namespace SageFne.Reader.Configuration;

/// <summary>
/// Classification des lignes à 0 % de TVA, du plus précis au plus général.
/// </summary>
/// <remarks>
/// Sage ne distingue pas l'exonération conventionnelle de l'exonération légale :
/// le taux vaut 0 dans les deux cas, et la vérification faite sur le dossier HT
/// l'a confirmé — <c>F_TAXE</c> n'a aucune fiche à taux 0. La distinction est un
/// fait juridique que Sage n'a jamais eu de raison d'enregistrer.
///
/// Ces règles ne sont consultées que sur une ligne <b>déjà constatée à 0 %</b>.
/// Aucune d'elles ne peut détaxer une ligne : une ligne à 9 % ou 18 % ne les lit
/// jamais.
///
/// Elle se déclare donc ici. La structure est volontairement plate — quatre
/// dictionnaires de chaînes — pour qu'une interface de paramétrage puisse les
/// alimenter plus tard sans que le code change : c'est
/// <see cref="Mapping.IZeroVatPolicy"/> qui compte, pas d'où viennent les règles.
/// </remarks>
public sealed class ZeroVatOptions
{
    /// <summary>
    /// Régime fiscal déclaré de l'acheteur, par compte client (CT_Num).
    /// <b>Priorité absolue.</b>
    /// </summary>
    /// <remarks>
    /// Valeurs admises : <c>TEE</c> et <c>RME</c>. Les deux partagent le même
    /// fondement juridique et donnent l'un comme l'autre
    /// <see cref="Mapping.CodeTvaZero.Tvad"/>, code FNE <c>TVAD</c>.
    ///
    /// Ce régime prime sur les règles d'article et de famille, parce qu'il ne
    /// dit pas la même chose qu'elles : celles-ci expliquent pourquoi un
    /// <i>produit</i> n'est pas taxé, celui-là pourquoi un <i>acheteur</i> ne
    /// l'est pas. Quand les deux s'appliquent, c'est le statut de l'acheteur qui
    /// fonde l'exonération, et c'est lui qu'il faut déclarer à la DGI.
    ///
    /// Il se déclare, et ne se déduit jamais. Un client dont toutes les factures
    /// sont à 0 % n'est pas un client TEE : c'est un client dont on ignore le
    /// régime. L'audit expose cette régularité, il ne la transforme pas en
    /// règle — rien dans le code ne lit l'historique pour alimenter ce
    /// dictionnaire.
    /// </remarks>
    public Dictionary<string, string> CustomerTaxRegimes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Règle du dossier, appliquée à défaut de plus précis.
    /// </summary>
    /// <remarks>
    /// Comme les trois dictionnaires ci-dessous, elle porte un <b>code FNE</b> —
    /// <c>Tvac</c>, <c>Tvad</c> ou <c>Unknown</c> — et non une qualification
    /// juridique. Les anciens noms <c>ConventionalExemption</c> et
    /// <c>LegalExemptionTEE_RME</c> restent acceptés, mais signalés : ils
    /// nommaient un fondement là où seul un code a sa place, et l'inscrire sur
    /// un article de poisson congelé affirmait un régime TEE/RME que rien ne
    /// justifie.
    /// </remarks>
    public string Default { get; set; } = "Unknown";

    /// <summary>Par référence d'article (AR_Ref). Priorité 2.</summary>
    public Dictionary<string, string> ByArticle { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Par famille d'article (FA_CodeFamille). Priorité 3.</summary>
    public Dictionary<string, string> ByFamily { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Par compte client (CT_Num), pour une exonération qui tient au client sans
    /// relever de TEE/RME. Priorité 4.
    /// </summary>
    public Dictionary<string, string> ByCustomer { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
