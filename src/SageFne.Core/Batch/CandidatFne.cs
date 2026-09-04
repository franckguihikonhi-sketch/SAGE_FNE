using SageFne.Core.Mapping;
using SageFne.Core.Validation;

namespace SageFne.Core.Batch;

/// <summary>
/// Pourquoi une facture ne peut pas servir de cas d'essai.
/// </summary>
/// <param name="Code">
/// Repère stable, pour compter combien de pièces butent sur le même mur.
/// Cinq pièces prises au hasard ne disent pas s'il y en a douze ou huit cents.
/// </param>
public sealed record Disqualification(string Code, string Message)
{
    public const string NonTraduite = "NON_TRADUITE";
    public const string ErreursControle = "ERREURS_CONTROLE";
    public const string TauxAbsent = "TAUX_CHERCHE_ABSENT";
    public const string TvaZero = "LIGNE_TVA_ZERO";
    public const string HorsNomenclature = "TAUX_HORS_NOMENCLATURE";
    public const string NccAbsent = "NCC_ABSENT";
    public const string DejaAuRegistre = "DEJA_AU_REGISTRE";
}

/// <summary>Le taux de TVA qu'un candidat doit démontrer.</summary>
public enum TauxRecherche
{
    Normal = 18,
    Reduit = 9,
}

/// <summary>
/// Une facture réelle proposée comme cas d'essai pour le premier envoi.
/// </summary>
/// <remarks>
/// Le premier envoi à la DGI est irréversible : ce qui part certifié ne se
/// corrige que par un avoir. La pièce d'essai doit donc être la moins
/// discutable du dossier — fiscalement nette, arithmétiquement juste, et courte
/// assez pour être vérifiée à l'œil.
///
/// La notation est explicite plutôt que subtile : chaque point gagné ou perdu
/// se lit dans <see cref="Raisons"/>. Un candidat qu'on ne comprend pas n'en
/// est pas un.
/// </remarks>
public sealed class CandidatFne
{
    public required InvoiceConversion Conversion { get; init; }
    public required TauxRecherche Taux { get; init; }

    /// <summary>Les taux de TVA rencontrés sur les lignes.</summary>
    public required IReadOnlyList<decimal> TauxRencontres { get; init; }

    /// <summary>Les prélèvements repris, par leur nom FNE.</summary>
    public required IReadOnlyList<string> CustomTaxes { get; init; }

    public required int Score { get; init; }
    public required IReadOnlyList<string> Raisons { get; init; }

    /// <summary>Ce qui l'écarte définitivement. Vide : le candidat tient.</summary>
    public required IReadOnlyList<Disqualification> Disqualifications { get; init; }

    public bool Retenu => Disqualifications.Count == 0;

    /// <summary>Le candidat porte-t-il ce motif d'exclusion ?</summary>
    public bool Ecarte(string code) =>
        Disqualifications.Any(motif => motif.Code == code);

    /// <summary>Écart entre le TTC des lignes et celui de l'entête.</summary>
    public decimal EcartTTC => Conversion.TotalTTC - Conversion.Header.TotalTTC;

    public string Statut => Retenu
        ? Conversion.Report.Constats.Count == 0 ? "net" : "réserves"
        : "écarté";

    /// <summary>
    /// Départage les candidats d'un même taux.
    /// </summary>
    /// <remarks>
    /// Rien d'ésotérique : une pièce sans le moindre constat, aux totaux justes,
    /// à taux unique et courte passe devant. Les points sont attribués dans cet
    /// ordre d'importance, et chacun est justifié dans les raisons.
    /// </remarks>
    public static CandidatFne Evaluer(InvoiceConversion conversion, TauxRecherche taux, decimal tolerance)
    {
        var attendu = (decimal)(int)taux;
        var tauxRencontres = conversion.Lines
            .Select(TaxMapping.TauxTva)
            .Distinct()
            .Order()
            .ToList();

        var customTaxes = conversion.Invoice?.Items
            .SelectMany(item => item.CustomTaxes ?? [])
            .Select(taxe => taxe.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        var disqualifications = new List<Disqualification>();
        var raisons = new List<string>();
        var score = 0;

        // --- Ce qui écarte sans appel ---------------------------------------

        if (conversion.Invoice is null)
        {
            disqualifications.Add(new Disqualification(
                Disqualification.NonTraduite, "la facture n'a pas pu être traduite."));
        }

        if (conversion.Report.ContientDesErreurs)
        {
            var codes = conversion.Report.Constats
                .Where(constat => constat.Severite == Severite.Erreur)
                .Select(constat => constat.Code)
                .Distinct()
                .ToList();
            disqualifications.Add(new Disqualification(
                Disqualification.ErreursControle, $"erreurs de contrôle : {string.Join(", ", codes)}."));
        }

        if (!tauxRencontres.Contains(attendu))
        {
            disqualifications.Add(new Disqualification(
                Disqualification.TauxAbsent, $"aucune ligne à {attendu} % de TVA."));
        }

        // Une ligne à 0 % soulève la question TVAC/TVAD, qui n'est pas tranchée.
        if (tauxRencontres.Contains(0m))
        {
            disqualifications.Add(new Disqualification(
                Disqualification.TvaZero,
                "une ligne au moins est à 0 % de TVA : régime d'exonération non tranché."));
        }

        var horsNomenclature = tauxRencontres
            .Where(rencontre => rencontre != 0m && TaxMapping.CodeTva(rencontre) is null)
            .ToList();
        if (horsNomenclature.Count > 0)
        {
            disqualifications.Add(new Disqualification(
                Disqualification.HorsNomenclature,
                $"taux hors nomenclature FNE : {string.Join(", ", horsNomenclature.Select(t => $"{t} %"))}."));
        }

        if (string.IsNullOrWhiteSpace(conversion.Customer?.Identifiant))
        {
            disqualifications.Add(new Disqualification(
                Disqualification.NccAbsent, "NCC absent : la facture ne peut pas partir en B2B."));
        }

        // --- Ce qui départage -----------------------------------------------

        if (conversion.Report.Constats.Count == 0)
        {
            score += 100;
            raisons.Add("aucun constat, ni erreur ni réserve");
        }
        else
        {
            var reserves = conversion.Report.Constats.Count;
            score -= reserves * 15;
            raisons.Add($"{reserves} réserve(s) : {string.Join(", ",
                conversion.Report.Constats.Select(constat => constat.Code).Distinct())}");
        }

        var ecart = Math.Abs(conversion.TotalTTC - conversion.Header.TotalTTC);
        if (ecart <= tolerance)
        {
            score += 60;
            raisons.Add("total TTC des lignes conforme à DO_TotalTTC");
        }
        else
        {
            score -= 40;
            raisons.Add($"écart de {ecart:0.##} entre le TTC des lignes et DO_TotalTTC");
        }

        if (tauxRencontres.Count == 1)
        {
            score += 40;
            raisons.Add($"taux unique de {attendu} %");
        }
        else
        {
            raisons.Add($"plusieurs taux : {string.Join(", ", tauxRencontres.Select(t => $"{t} %"))}");
        }

        // Peu de lignes : une pièce d'essai se vérifie à l'œil.
        var lignes = conversion.Lines.Count;
        score += lignes switch
        {
            1 => 40,
            2 or 3 => 25,
            <= 5 => 10,
            <= 10 => 0,
            _ => -20,
        };
        raisons.Add($"{lignes} ligne(s)");

        if (customTaxes.Count == 0)
        {
            score += 15;
            raisons.Add("aucun prélèvement");
        }
        else
        {
            raisons.Add($"prélèvement(s) : {string.Join(", ", customTaxes)}");
        }

        if (conversion.Etat == EtatPiece.DejaCertifiee || conversion.Etat == EtatPiece.ModifieeDepuis)
        {
            disqualifications.Add(new Disqualification(
                Disqualification.DejaAuRegistre, $"déjà connue du registre ({conversion.LibelleEtat})."));
        }

        return new CandidatFne
        {
            Conversion = conversion,
            Taux = taux,
            TauxRencontres = tauxRencontres,
            CustomTaxes = customTaxes,
            Score = score,
            Raisons = raisons,
            Disqualifications = disqualifications,
        };
    }
}
