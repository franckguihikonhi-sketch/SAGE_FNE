using SageFne.Reader.Mapping;
using SageFne.Reader.Models.Sage;

namespace SageFne.Reader.Validation;

/// <summary>
/// Recalcule ce que Sage a stocké, et signale les écarts.
/// </summary>
/// <remarks>
/// Un écart ne bloque pas : il se peut que ce soit notre lecture qui soit
/// incomplète — une remise dont le type n'est pas encore lu, par exemple. Mais
/// il doit se voir, parce qu'une ligne fausse certifiée par la DGI ne se
/// corrige plus.
/// </remarks>
public static class FinancialChecks
{
    /// <summary>Tolérance d'arrondi, en francs CFA.</summary>
    public const decimal Tolerance = 1m;

    public static void Run(IReadOnlyCollection<SageDocumentLine> lignes, CheckReport rapport)
    {
        foreach (var ligne in lignes)
        {
            var ou = $"ligne {ligne.Ligne}";
            var brut = ligne.Quantite * ligne.PrixUnitaire;
            var remises = ligne.Remise1 != 0m || ligne.Remise2 != 0m || ligne.Remise3 != 0m;

            if (remises)
            {
                // Sage range le type de remise (pourcentage ou valeur) dans
                // DL_Remise0N_REM_Type, que nous ne lisons pas encore : le
                // contrôle serait faux une fois sur deux. On le dit plutôt que
                // de valider à tort.
                rapport.Avertir(
                    "REMISE_NON_INTERPRETEE",
                    $"{ou} : remises {ligne.Remise1}/{ligne.Remise2}/{ligne.Remise3} présentes. " +
                    "Le type de remise (% ou valeur) n'est pas encore lu : contrôle du HT non concluant.");
            }
            else
            {
                var ecart = brut - ligne.MontantHT;
                if (Math.Abs(ecart) > Tolerance)
                {
                    rapport.Avertir(
                        "ECART_HT",
                        $"{ou} : {ligne.Quantite} x {ligne.PrixUnitaire} = {brut}, " +
                        $"mais DL_MontantHT vaut {ligne.MontantHT} (écart de {ecart}).");
                }
            }

            var attenduTTC = ligne.MontantHT * (1m + TaxMapping.TauxCumule(ligne) / 100m);
            var ecartTTC = attenduTTC - ligne.MontantTTC;
            if (Math.Abs(ecartTTC) > Tolerance)
            {
                rapport.Avertir(
                    "ECART_TTC",
                    $"{ou} : {ligne.MontantHT} majoré de {TaxMapping.TauxCumule(ligne)} % donne {attenduTTC}, " +
                    $"mais DL_MontantTTC vaut {ligne.MontantTTC} (écart de {ecartTTC}).");
            }
        }
    }

    /// <summary>
    /// DO_TotalHT vaut 0 sur une partie des documents du dossier : le total de
    /// référence est celui des lignes. Le contrôle le dit quand les deux
    /// diffèrent, sans traiter l'entête comme la vérité.
    /// </summary>
    public static void CompareHeader(
        SageDocumentHeader entete,
        IReadOnlyCollection<SageDocumentLine> lignes,
        CheckReport rapport)
    {
        var totalLignes = lignes.Sum(ligne => ligne.MontantHT);

        if (entete.TotalHT == 0m && totalLignes != 0m)
        {
            rapport.Avertir(
                "ENTETE_HT_NUL",
                $"DO_TotalHT vaut 0 alors que les lignes totalisent {totalLignes} : " +
                "le total des lignes fait foi.");
            return;
        }

        var ecart = totalLignes - entete.TotalHT;
        if (Math.Abs(ecart) > Tolerance)
        {
            rapport.Avertir(
                "ECART_ENTETE_HT",
                $"DO_TotalHT vaut {entete.TotalHT}, les lignes totalisent {totalLignes} (écart de {ecart}).");
        }
    }
}
