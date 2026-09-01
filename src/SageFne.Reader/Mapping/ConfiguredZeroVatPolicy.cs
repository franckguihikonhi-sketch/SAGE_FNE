using SageFne.Reader.Configuration;

namespace SageFne.Reader.Mapping;

/// <summary>
/// Applique les règles de <see cref="ZeroVatOptions"/>, du plus précis au plus
/// général.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>le <b>régime fiscal déclaré de l'acheteur</b> — TEE ou RME ;</item>
/// <item>l'article — l'exonération tient au produit précis ;</item>
/// <item>sa famille — elle tient à toute une gamme ;</item>
/// <item>le client — elle tient à ce client sans relever de TEE/RME ;</item>
/// <item>le dossier — elle vaut pour toute l'entreprise ;</item>
/// <item>rien : <see cref="RegimeTvaZero.Inconnu"/>, et la pièce est bloquée.</item>
/// </list>
///
/// Le régime de l'acheteur passe avant l'article, et ce n'est pas une question
/// de finesse : les deux ne disent pas la même chose. Une règle d'article
/// explique pourquoi un <i>produit</i> n'est pas taxé ; le régime de l'acheteur,
/// pourquoi un <i>acheteur</i> ne l'est pas. Quand les deux s'appliquent, c'est
/// le statut de l'acheteur qui fonde l'exonération devant la DGI.
///
/// Aucune de ces règles ne détaxe quoi que ce soit : elles ne sont consultées
/// que sur une ligne déjà constatée à 0 %. Une ligne à 9 % ou 18 % ne les lit
/// jamais.
///
/// Une valeur de paramétrage qui n'est ni <c>ConventionalExemption</c> ni
/// <c>LegalExemptionTEE_RME</c> est <b>refusée et signalée</b>. Passer au niveau
/// suivant reviendrait à traiter une faute de frappe comme une absence de
/// règle : la facture partirait sous un régime que personne n'a voulu.
/// </remarks>
public sealed class ConfiguredZeroVatPolicy(ZeroVatOptions options) : IZeroVatPolicy
{
    public const string Conventionnelle = "ConventionalExemption";
    public const string Legale = "LegalExemptionTEE_RME";
    public const string Aucune = "Unknown";

    /// <summary>Régimes d'acheteur reconnus, tous deux de fondement légal.</summary>
    public const string RegimeTee = "TEE";
    public const string RegimeRme = "RME";

    public ZeroVatDecision Decider(ZeroVatContexte contexte)
    {
        // Le régime de l'acheteur d'abord : il prime sur la nature du produit.
        if (!string.IsNullOrWhiteSpace(contexte.CtNum)
            && options.CustomerTaxRegimes.TryGetValue(contexte.CtNum.Trim(), out var declare))
        {
            var regimeAcheteur = AnalyserRegimeAcheteur(declare);
            if (regimeAcheteur is null)
            {
                return new ZeroVatDecision(
                    RegimeTvaZero.Inconnu,
                    $"régime acheteur {contexte.CtNum}",
                    $"le régime déclaré du client {contexte.CtNum} vaut « {declare} », qui n'est " +
                    $"pas un régime reconnu. Seuls {RegimeTee} et {RegimeRme} sont acceptés.");
            }

            return new ZeroVatDecision(
                regimeAcheteur.Value,
                $"régime acheteur {declare.Trim().ToUpperInvariant()} du client {contexte.CtNum}");
        }

        foreach (var (table, cle, origine) in new[]
                 {
                     (options.ByArticle, contexte.ArticleReference, "article"),
                     (options.ByFamily, contexte.Famille, "famille"),
                     (options.ByCustomer, contexte.CtNum, "client"),
                 })
        {
            if (string.IsNullOrWhiteSpace(cle) || !table.TryGetValue(cle.Trim(), out var valeur)) continue;

            var regime = Analyser(valeur);
            if (regime is null)
            {
                return new ZeroVatDecision(
                    RegimeTvaZero.Inconnu,
                    $"{origine} {cle}",
                    $"la règle « {origine} {cle} » vaut « {valeur} », qui n'est pas une classification " +
                    $"reconnue. Seuls {Conventionnelle} et {Legale} sont acceptés.");
            }

            // Unknown déclaré explicitement : la règle existe et dit « je ne
            // sais pas ». Elle décide quand même, et bloque.
            return new ZeroVatDecision(regime.Value, $"{origine} {cle}");
        }

        var dossier = Analyser(options.Default);
        if (dossier is null)
        {
            return new ZeroVatDecision(
                RegimeTvaZero.Inconnu,
                "dossier",
                $"le réglage du dossier vaut « {options.Default} », qui n'est pas une classification " +
                $"reconnue. Seuls {Conventionnelle} et {Legale} sont acceptés.");
        }

        return new ZeroVatDecision(
            dossier.Value,
            dossier.Value == RegimeTvaZero.Inconnu ? "aucune règle applicable" : "dossier");
    }

    /// <summary>
    /// Les deux seules classifications, plus l'absence de classification.
    /// </summary>
    /// <returns>
    /// <c>null</c> quand la valeur n'est reconnue d'aucune façon — à distinguer
    /// de <see cref="RegimeTvaZero.Inconnu"/>, qui est un choix délibéré.
    /// </returns>
    public static RegimeTvaZero? Analyser(string? valeur) => valeur?.Trim() switch
    {
        Conventionnelle => RegimeTvaZero.ExonerationConventionnelle,
        Legale => RegimeTvaZero.ExonerationLegaleTeeRme,
        Aucune or "" or null => RegimeTvaZero.Inconnu,
        _ => null,
    };

    /// <summary>
    /// Le régime fiscal d'un acheteur, tel qu'il se déclare.
    /// </summary>
    /// <remarks>
    /// TEE et RME partagent le même fondement juridique et donnent donc la même
    /// classification. Les distinguer ici n'aurait aucun effet sur le code FNE
    /// envoyé ; les accepter tous deux évite qu'un exploitant ait à traduire son
    /// vocabulaire dans le nôtre.
    ///
    /// Une chaîne vide n'est pas un régime : c'est une ligne de paramétrage
    /// laissée en plan, et elle est refusée comme telle plutôt que traitée comme
    /// une absence de règle — sans quoi elle passerait silencieusement à
    /// l'article suivant.
    /// </remarks>
    /// <returns><c>null</c> quand la valeur n'est pas un régime reconnu.</returns>
    public static RegimeTvaZero? AnalyserRegimeAcheteur(string? valeur) =>
        valeur?.Trim().ToUpperInvariant() switch
        {
            RegimeTee or RegimeRme => RegimeTvaZero.ExonerationLegaleTeeRme,
            _ => null,
        };
}
