using SageFne.Reader.Configuration;

namespace SageFne.Reader.Mapping;

/// <summary>
/// Applique les règles de <see cref="ZeroVatOptions"/>, du plus précis au plus
/// général.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>l'article — l'exonération tient au produit précis ;</item>
/// <item>sa famille — elle tient à toute une gamme ;</item>
/// <item>le client — elle tient au titulaire d'un régime ;</item>
/// <item>le dossier — elle vaut pour toute l'entreprise ;</item>
/// <item>rien : <see cref="RegimeTvaZero.Inconnu"/>, et la pièce est bloquée.</item>
/// </list>
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

    public ZeroVatDecision Decider(ZeroVatContexte contexte)
    {
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
}
