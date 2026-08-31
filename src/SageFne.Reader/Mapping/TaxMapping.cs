using SageFne.Reader.Models.Fne;
using SageFne.Reader.Models.Sage;

namespace SageFne.Reader.Mapping;

/// <summary>
/// Traduction des taxes d'une ligne Sage vers la nomenclature FNE.
/// </summary>
/// <remarks>
/// Le code taxe du dossier ne suffit pas à décider : DL_CodeTaxe1 vaut « TVA »
/// aussi bien pour 9 % que pour 18 %, et la fiche F_TAXE qui porte ce code est
/// intitulée « TVA/VENTE » à 9 %. C'est donc le <b>taux porté par la ligne</b>
/// qui tranche, jamais l'intitulé.
///
/// Les trois emplacements de taxe de Sage sont examinés : rien ne garantit que
/// la TVA soit toujours en position 1 et l'AIRSI en position 2.
///
/// Une ligne sans TVA n'est pas une ligne sans code : FNE attend un code
/// d'exonération. Mais <b>lequel ne se déduit pas du taux</b> — <c>TVAC</c>
/// pour l'exonération conventionnelle et <c>TVAD</c> pour l'exonération légale
/// TEE/RME valent tous deux 0 %, et Sage ne porte pas la différence. Le régime
/// doit donc être fourni de l'extérieur ; à défaut, la ligne reste sans code et
/// la pièce est bloquée.
/// </remarks>
public static class TaxMapping
{
    /// <summary>Taux normal de la TVA ivoirienne, code FNE « TVA ».</summary>
    public const decimal TauxNormal = 18m;

    /// <summary>Taux réduit, code FNE « TVAB ».</summary>
    public const decimal TauxReduit = 9m;

    /// <summary>Écart admis entre le taux lu et un taux de la nomenclature.</summary>
    private const decimal Tolerance = 0.001m;

    /// <summary>Le constat qui empêche une pièce de partir.</summary>
    public const string CodeRegimeInconnu = "ZERO_VAT_CATEGORY_UNKNOWN";

    /// <summary>Prélèvements qui ne sont pas une TVA et passent en customTaxes.</summary>
    private static readonly string[] Prelevements = ["AIRSI"];

    /// <param name="RegimeZeroRequis">
    /// La ligne est à 0 % de TVA et aucun régime ne lui est attribué : elle ne
    /// porte donc aucun code, et la pièce ne peut pas être certifiée.
    /// </param>
    public sealed record Resultat(
        IReadOnlyList<string> Taxes,
        IReadOnlyList<FneCustomTax> CustomTaxes,
        IReadOnlyList<string> Avertissements,
        bool RegimeZeroRequis = false);

    /// <param name="regimeZero">
    /// Régime qui justifie une TVA à 0 % sur cette ligne. <see
    /// cref="RegimeTvaZero.Inconnu"/> par défaut : le taux seul ne permet pas de
    /// choisir entre TVAC et TVAD, et deviner reviendrait à déclarer à la DGI un
    /// régime fiscal qu'on ignore.
    /// </param>
    public static Resultat Read(SageDocumentLine ligne, RegimeTvaZero regimeZero = RegimeTvaZero.Inconnu)
    {
        var taxes = new List<string>();
        var custom = new List<FneCustomTax>();
        var avertissements = new List<string>();
        // Un taux positif que la nomenclature ne connaît pas n'est pas une
        // exonération : la ligne ne doit surtout pas partir en TVAD.
        var tauxInconnu = false;

        foreach (var taxe in ligne.Taxes())
        {
            if (!taxe.EstRenseignee) continue;

            if (EstPrelevement(taxe.Code))
            {
                if (taxe.Taux > 0m) custom.Add(new FneCustomTax(taxe.Code.Trim().ToUpperInvariant(), taxe.Taux));
                continue;
            }

            var code = CodeTva(taxe.Taux);
            if (code is not null)
            {
                if (!taxes.Contains(code)) taxes.Add(code);
                continue;
            }

            // Un taux qui n'est ni 18, ni 9, ni 0 : on ne l'invente pas.
            if (taxe.Taux != 0m)
            {
                tauxInconnu = true;
                avertissements.Add(
                    $"ligne {ligne.Ligne} : taux de {taxe.Taux} % à l'emplacement {taxe.Emplacement} " +
                    $"(code « {taxe.Code} ») hors nomenclature FNE, il n'est pas repris.");
            }
        }

        // Aucun taux reconnu et aucun taux aberrant : la ligne est à 0 %. C'est
        // ici, et seulement ici, que le régime d'exonération entre en jeu.
        var regimeZeroRequis = false;
        if (taxes.Count == 0 && !tauxInconnu)
        {
            var code = regimeZero.Code();
            if (code is not null)
            {
                taxes.Add(code);
            }
            else
            {
                regimeZeroRequis = true;
                avertissements.Add(
                    $"ligne {ligne.Ligne} : TVA 0 % détectée mais impossible de déterminer " +
                    "TVAC (exonération conventionnelle) ou TVAD (exonération légale TEE/RME).");
            }
        }

        return new Resultat(taxes, custom, avertissements, regimeZeroRequis);
    }

    /// <summary>« TVA » à 18 %, « TVAB » à 9 %, rien du tout à 0 %.</summary>
    public static string? CodeTva(decimal taux)
    {
        if (Math.Abs(taux - TauxNormal) <= Tolerance) return "TVA";
        if (Math.Abs(taux - TauxReduit) <= Tolerance) return "TVAB";
        return null;
    }

    public static bool EstPrelevement(string code) =>
        Prelevements.Any(nom => string.Equals(nom, code.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Somme des taux appliqués à la ligne, TVA et prélèvements confondus.</summary>
    public static decimal TauxCumule(SageDocumentLine ligne) =>
        ligne.Taxes().Where(taxe => taxe.EstRenseignee).Sum(taxe => taxe.Taux);

    /// <summary>
    /// Le taux de TVA de la ligne : celui des emplacements qui n'est pas un
    /// prélèvement. Zéro quand la ligne est exonérée.
    /// </summary>
    public static decimal TauxTva(SageDocumentLine ligne) =>
        ligne.Taxes()
            .Where(taxe => taxe.EstRenseignee && !EstPrelevement(taxe.Code))
            .Sum(taxe => taxe.Taux);

    /// <summary>Le cumul des prélèvements de la ligne — l'AIRSI, aujourd'hui.</summary>
    public static decimal TauxPrelevements(SageDocumentLine ligne) =>
        ligne.Taxes()
            .Where(taxe => taxe.EstRenseignee && EstPrelevement(taxe.Code))
            .Sum(taxe => taxe.Taux);
}
