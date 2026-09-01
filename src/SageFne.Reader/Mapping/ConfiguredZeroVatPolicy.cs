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
/// <item>rien : <see cref="CodeTvaZero.Inconnu"/>, et la pièce est bloquée.</item>
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
    /// <summary>Les valeurs de paramétrage : des codes FNE, et rien d'autre.</summary>
    public const string Tvac = "Tvac";
    public const string Tvad = "Tvad";
    public const string Aucune = "Unknown";

    /// <summary>
    /// Anciens noms, encore acceptés mais signalés.
    /// </summary>
    /// <remarks>
    /// Ils nommaient un fondement — « exonération conventionnelle », « TEE/RME »
    /// — là où seul un code avait sa place. Les garder évite de casser une
    /// configuration existante ; les signaler évite qu'une exonération de
    /// produit reste inscrite pour toujours sous un régime d'acheteur.
    /// </remarks>
    public const string ConventionnelleHeritee = "ConventionalExemption";
    public const string LegaleHeritee = "LegalExemptionTEE_RME";

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
                    CodeTvaZero.Inconnu,
                    $"régime acheteur {contexte.CtNum}",
                    $"le régime déclaré du client {contexte.CtNum} vaut « {declare} », qui n'est " +
                    $"pas un régime reconnu. Seuls {RegimeTee} et {RegimeRme} sont acceptés.");
            }

            return new ZeroVatDecision(
                regimeAcheteur.Value,
                $"régime acheteur {declare.Trim().ToUpperInvariant()} du client {contexte.CtNum}",
                Fondement: FondementExoneration.RegimeAcheteur);
        }

        foreach (var (table, cle, origine) in new[]
                 {
                     (options.ByArticle, contexte.ArticleReference, "article"),
                     (options.ByFamily, contexte.Famille, "famille"),
                     (options.ByCustomer, contexte.CtNum, "client"),
                 })
        {
            if (string.IsNullOrWhiteSpace(cle) || !table.TryGetValue(cle.Trim(), out var valeur)) continue;

            var code = Analyser(valeur);
            if (code is null)
            {
                return new ZeroVatDecision(
                    CodeTvaZero.Inconnu,
                    $"{origine} {cle}",
                    $"la règle « {origine} {cle} » vaut « {valeur} », qui n'est pas un code reconnu. " +
                    $"Seuls {Tvac}, {Tvad} et {Aucune} sont acceptés.");
            }

            // Unknown déclaré explicitement : la règle existe et dit « je ne
            // sais pas ». Elle décide quand même, et bloque.
            return new ZeroVatDecision(
                code.Value,
                $"{origine} {cle}",
                Avertissement: Herite(valeur, origine, cle));
        }

        var dossier = Analyser(options.Default);
        if (dossier is null)
        {
            return new ZeroVatDecision(
                CodeTvaZero.Inconnu,
                "dossier",
                $"le réglage du dossier vaut « {options.Default} », qui n'est pas un code reconnu. " +
                $"Seuls {Tvac}, {Tvad} et {Aucune} sont acceptés.");
        }

        return new ZeroVatDecision(
            dossier.Value,
            dossier.Value == CodeTvaZero.Inconnu ? "aucune règle applicable" : "dossier",
            Avertissement: Herite(options.Default, "dossier", ""));
    }

    /// <summary>
    /// Les deux seules classifications, plus l'absence de classification.
    /// </summary>
    /// <returns>
    /// <c>null</c> quand la valeur n'est reconnue d'aucune façon — à distinguer
    /// de <see cref="CodeTvaZero.Inconnu"/>, qui est un choix délibéré.
    /// </returns>
    /// <remarks>
    /// La casse est ignorée : le paramétrage porte désormais un code FNE, et
    /// « TVAD » est la graphie que la documentation de la DGI emploie. Refuser
    /// ce que tout le monde écrira naturellement serait perverse.
    /// </remarks>
    public static CodeTvaZero? Analyser(string? valeur) =>
        valeur?.Trim().ToUpperInvariant() switch
        {
            "TVAC" => CodeTvaZero.Tvac,
            "TVAD" => CodeTvaZero.Tvad,
            "CONVENTIONALEXEMPTION" => CodeTvaZero.Tvac,
            "LEGALEXEMPTIONTEE_RME" => CodeTvaZero.Tvad,
            "UNKNOWN" or "" or null => CodeTvaZero.Inconnu,
            _ => null,
        };

    /// <summary>
    /// L'avertissement dû à une valeur héritée, ou null.
    /// </summary>
    /// <remarks>
    /// <c>LegalExemptionTEE_RME</c> posé sur un article affirme que ce produit
    /// relève du régime TEE/RME de l'acheteur. Sur un article de poisson
    /// congelé, c'est faux, et la configuration est la piste d'audit. La règle
    /// décide quand même — casser un paramétrage existant serait pire — mais
    /// elle ne passe plus en silence.
    /// </remarks>
    private static string? Herite(string? valeur, string origine, string cle) =>
        valeur?.Trim().ToUpperInvariant() switch
    {
        "CONVENTIONALEXEMPTION" =>
            $"la règle « {$"{origine} {cle}".Trim()} » vaut « {ConventionnelleHeritee} », un ancien nom qui " +
            $"décrit un fondement juridique. Écrivez « {Tvac} » : le paramétrage porte un code FNE, " +
            "pas une qualification fiscale.",
        "LEGALEXEMPTIONTEE_RME" =>
            $"la règle « {$"{origine} {cle}".Trim()} » vaut « {LegaleHeritee} », un ancien nom qui affirme " +
            $"un régime TEE/RME. Écrivez « {Tvad} » si c'est le code voulu : le fondement se " +
            "documente ailleurs, et TEE/RME ne se déclare que dans CustomerTaxRegimes.",
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
    public static CodeTvaZero? AnalyserRegimeAcheteur(string? valeur) =>
        valeur?.Trim().ToUpperInvariant() switch
        {
            RegimeTee or RegimeRme => CodeTvaZero.Tvad,
            _ => null,
        };
}
