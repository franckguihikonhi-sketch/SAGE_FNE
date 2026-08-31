using SageFne.Reader.Models.Sage;

namespace SageFne.Reader.Mapping;

/// <summary>
/// Ce que le dossier dit de ses propres taxes, et ce que FNE en attend.
/// </summary>
/// <remarks>
/// Relevé sur le dossier HT : les trois fiches de <c>F_TAXE</c> portent toutes
/// <c>TA_EdiCode = "VAT"</c>, <b>y compris l'AIRSI</b>. Se fier à ce champ pour
/// reconnaître une TVA ferait certifier l'AIRSI comme de la TVA.
///
/// <c>TA_Regroup</c>, lui, sépare correctement : « TVA » pour les deux taux de
/// TVA, « AIRSI » pour le prélèvement. Il sert donc à <b>nommer le groupe</b>
/// d'un code — de quoi dire précisément ce qui bloque quand un code n'est pas
/// repris.
///
/// Il ne sert pas à décider seul : un code n'entre en <c>customTaxes</c> que
/// s'il est explicitement mappé. Convertir tout non-TVA en customTax enverrait
/// à la DGI des prélèvements sous un nom que personne n'a validé.
/// </remarks>
public sealed class TaxCatalogue
{
    private readonly Dictionary<string, string> _groupes;
    private readonly Dictionary<string, string> _customTaxes;

    public TaxCatalogue(
        IEnumerable<SageTaxDefinition>? fiches = null,
        IReadOnlyDictionary<string, string>? customTaxes = null)
    {
        _groupes = (fiches ?? [])
            .Where(fiche => !string.IsNullOrWhiteSpace(fiche.Code))
            .GroupBy(fiche => fiche.Code.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                groupe => groupe.Key,
                groupe => groupe.First().Regroupement.Trim(),
                StringComparer.OrdinalIgnoreCase);

        _customTaxes = new Dictionary<string, string>(
            customTaxes ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Sans F_TAXE ni paramétrage : l'AIRSI seul est repris.</summary>
    public static TaxCatalogue Defaut { get; } = new(
        customTaxes: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["AIRSI"] = "AIRSI" });

    /// <summary>Le nom FNE d'un prélèvement, ou null s'il n'est pas mappé.</summary>
    public string? NomFne(string codeSage) =>
        !string.IsNullOrWhiteSpace(codeSage) && _customTaxes.TryGetValue(codeSage.Trim(), out var nom)
            ? nom
            : null;

    /// <summary>TA_Regroup du code, ou une chaîne vide si F_TAXE n'a pas été lue.</summary>
    public string Groupe(string codeSage) =>
        !string.IsNullOrWhiteSpace(codeSage) && _groupes.TryGetValue(codeSage.Trim(), out var groupe)
            ? groupe
            : "";

    /// <summary>
    /// Les codes mappés qui partagent le groupe de celui-ci. Sert à dire
    /// « AIB est du même groupe qu'AIRSI, qui lui est repris » plutôt que de
    /// laisser l'exploitant chercher.
    /// </summary>
    public IReadOnlyList<string> MappesDuMemeGroupe(string codeSage)
    {
        var groupe = Groupe(codeSage);
        if (groupe == "") return [];

        return _customTaxes.Keys
            .Where(code => !string.Equals(code, codeSage.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(code => string.Equals(Groupe(code), groupe, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
