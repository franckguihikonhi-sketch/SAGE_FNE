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
            var remise = RemiseMapping.Read(ligne);
            var brut = ligne.Quantite * remise.PrixUnitaireNet;
            var ecart = brut - ligne.MontantHT;

            if (Math.Abs(ecart) > Tolerance)
            {
                var detail = remise.Remisee
                    ? $" (prix net après remise {remise.Description})"
                    : "";
                rapport.Avertir(
                    "ECART_HT",
                    $"{ou} : {ligne.Quantite} x {remise.PrixUnitaireNet}{detail} = {brut}, " +
                    $"mais DL_MontantHT vaut {ligne.MontantHT} (écart de {ecart}).");
            }

            // FNE ne reçoit pas le montant de la ligne : il reçoit une quantité et
            // un prix unitaire, et refait la multiplication. Quand ce produit n'est
            // pas un nombre entier de francs, le total certifié dépend d'une règle
            // d'arrondi que la DGI ne publie pas — et le franc CFA n'a pas de
            // centimes. La facture certifiée peut alors différer de celle remise au
            // client.
            var centimes = brut - decimal.Truncate(brut);
            if (centimes != 0m && ligne.Quantite != 0m)
            {
                var arrondi = Math.Round(remise.PrixUnitaireNet, 2, MidpointRounding.AwayFromZero);
                var siArrondi = ligne.Quantite * arrondi;

                rapport.Avertir(
                    "ARRONDI_NON_TRANCHE",
                    $"{ou} : {ligne.Quantite} x {remise.PrixUnitaireNet} = {brut}, " +
                    $"qui n'est pas un nombre entier de francs. Sur la pièce 1052, la plateforme " +
                    $"a arrondi le total de ligne au franc le plus proche, et non le prix " +
                    $"unitaire : elle donnerait ici {Math.Round(brut, MidpointRounding.AwayFromZero)}. " +
                    $"Si elle arrondissait le prix unitaire à deux décimales, elle calculerait " +
                    $"{siArrondi} (écart de {brut - siArrondi}). Un seul cas observé ne fait pas " +
                    "une règle : comparez son total au vôtre.");
            }

            // Une remise dont le recalcul concorde avec le net de Sage confirme
            // notre lecture des types. On le note : c'est ce qui permettra de
            // cesser de s'en méfier une fois vu sur de vraies pièces.
            if (remise.Remisee && remise.Concordante)
            {
                rapport.Avertir(
                    "REMISE_APPLIQUEE",
                    $"{ou} : remise {remise.Description} sur {ligne.PrixUnitaire} — " +
                    $"prix net {remise.PrixUnitaireNet} envoyé, conforme au montant calculé par Sage.");
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
