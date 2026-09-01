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

    /// <summary>Le constat d'un prélèvement que rien ne dit comment nommer.</summary>
    public const string CodePrelevementSansMapping = "PRELEVEMENT_SANS_MAPPING_FNE";

    /// <param name="RegimeZeroRequis">
    /// La ligne est à 0 % de TVA et aucun régime ne lui est attribué : elle ne
    /// porte donc aucun code, et la pièce ne peut pas être certifiée.
    /// </param>
    /// <param name="PrelevementsSansMapping">
    /// Des codes que le dossier range avec un prélèvement repris, mais qu'aucun
    /// mapping FNE ne nomme. Ils ne partent pas, et bloquent la pièce.
    /// </param>
    public sealed record Resultat(
        IReadOnlyList<string> Taxes,
        IReadOnlyList<FneCustomTax> CustomTaxes,
        IReadOnlyList<string> Avertissements,
        bool RegimeZeroRequis = false,
        IReadOnlyList<string>? PrelevementsSansMapping = null)
    {
        public IReadOnlyList<string> PrelevementsSansMapping { get; init; } = PrelevementsSansMapping ?? [];
    }

    /// <param name="codeZero">
    /// Régime qui justifie une TVA à 0 % sur cette ligne. <see
    /// cref="CodeTvaZero.Inconnu"/> par défaut : le taux seul ne permet pas de
    /// choisir entre TVAC et TVAD, et deviner reviendrait à déclarer à la DGI un
    /// régime fiscal qu'on ignore.
    /// </param>
    /// <param name="catalogue">
    /// Ce que le dossier dit de ses taxes, et le mapping FNE des prélèvements.
    /// Par défaut, l'AIRSI seul.
    /// </param>
    public static Resultat Read(
        SageDocumentLine ligne,
        CodeTvaZero codeZero = CodeTvaZero.Inconnu,
        TaxCatalogue? catalogue = null)
    {
        var taxons = catalogue ?? TaxCatalogue.Defaut;
        var taxes = new List<string>();
        var custom = new List<FneCustomTax>();
        var avertissements = new List<string>();
        var prelevementsSansMapping = new List<string>();
        // Un taux positif que la nomenclature ne connaît pas n'est pas une
        // exonération : la ligne ne doit surtout pas partir en TVAD.
        var tauxInconnu = false;

        foreach (var taxe in ligne.Taxes())
        {
            if (!taxe.EstRenseignee) continue;

            // Un prélèvement explicitement mappé part sous le nom convenu.
            if (taxons.NomFne(taxe.Code) is { } nomFne)
            {
                if (taxe.Taux > 0m) custom.Add(new FneCustomTax(nomFne, taxe.Taux));
                continue;
            }

            var code = CodeTva(taxe.Taux);
            if (code is not null)
            {
                if (!taxes.Contains(code)) taxes.Add(code);
                continue;
            }

            // Un taux qui n'est ni 18, ni 9, ni 0 : on ne l'invente pas. Reste
            // à dire de quoi il s'agit — et c'est TA_Regroup qui le sait.
            if (taxe.Taux != 0m)
            {
                tauxInconnu = true;
                var groupe = taxons.Groupe(taxe.Code);
                var voisins = taxons.MappesDuMemeGroupe(taxe.Code);

                if (voisins.Count > 0)
                {
                    // Même groupe qu'un prélèvement repris : c'en est un, mais
                    // sous quel nom l'envoyer ? Personne ne l'a dit.
                    prelevementsSansMapping.Add(
                        $"ligne {ligne.Ligne} : le code « {taxe.Code} » ({taxe.Taux} %) appartient au " +
                        $"regroupement « {groupe} », comme {string.Join(", ", voisins)} qui est repris en " +
                        "customTaxes. Aucun nom FNE ne lui est associé : ajoutez-le à Fne:CustomTaxes " +
                        "plutôt que de le laisser deviner.");
                }
                else
                {
                    avertissements.Add(
                        $"ligne {ligne.Ligne} : taux de {taxe.Taux} % à l'emplacement {taxe.Emplacement} " +
                        $"(code « {taxe.Code} »{(groupe == "" ? "" : $", regroupement « {groupe} »")}) " +
                        "hors nomenclature FNE, il n'est pas repris.");
                }
            }
        }

        // Aucun taux reconnu et aucun taux aberrant : la ligne est à 0 %. C'est
        // ici, et seulement ici, que le régime d'exonération entre en jeu.
        var regimeZeroRequis = false;
        if (taxes.Count == 0 && !tauxInconnu)
        {
            var code = codeZero.Code();
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

        return new Resultat(
            taxes, custom, avertissements, regimeZeroRequis, prelevementsSansMapping);
    }

    /// <summary>« TVA » à 18 %, « TVAB » à 9 %, rien du tout à 0 %.</summary>
    public static string? CodeTva(decimal taux)
    {
        if (Math.Abs(taux - TauxNormal) <= Tolerance) return "TVA";
        if (Math.Abs(taux - TauxReduit) <= Tolerance) return "TVAB";
        return null;
    }

    public static bool EstPrelevement(string code, TaxCatalogue? catalogue = null) =>
        (catalogue ?? TaxCatalogue.Defaut).NomFne(code) is not null;

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
