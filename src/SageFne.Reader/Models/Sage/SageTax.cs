namespace SageFne.Reader.Models.Sage;

/// <summary>
/// Une taxe portée par une ligne : son emplacement Sage, son code et son taux.
/// </summary>
/// <param name="Emplacement">1, 2 ou 3, selon la colonne Sage d'origine.</param>
/// <param name="Code">DL_CodeTaxeN, tel que le dossier l'a paramétré.</param>
/// <param name="Taux">DL_TaxeN, en pourcentage.</param>
public readonly record struct SageTax(int Emplacement, string Code, decimal Taux)
{
    public bool EstRenseignee => Taux != 0m || !string.IsNullOrWhiteSpace(Code);
}

/// <summary>
/// Fiche de la table F_TAXE. Lue pour information : le mapping FNE se fonde
/// sur le taux porté par la ligne, jamais sur l'intitulé de cette fiche.
/// </summary>
public sealed class SageTaxDefinition
{
    public required string Code { get; init; }
    public string Intitule { get; init; } = "";
    public decimal Taux { get; init; }
    public short Type { get; init; }
    public string CompteGeneral { get; init; } = "";
    public string Regroupement { get; init; } = "";
    public string EdiCode { get; init; } = "";
}
