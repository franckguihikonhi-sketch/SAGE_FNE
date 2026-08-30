namespace SageFne.Reader.Models.Sage;

/// <summary>
/// Ligne de document, lue dans F_DOCLIGNE.
/// </summary>
/// <remarks>
/// Les trois emplacements de taxe de Sage sont repris tels quels. Rien
/// n'impose que la TVA soit en position 1 et l'AIRSI en position 2 : le
/// mapping les lit tous les trois.
/// </remarks>
public sealed class SageDocumentLine
{
    public required short Domaine { get; init; }
    public required short Type { get; init; }
    public required string Piece { get; init; }
    public required int Ligne { get; init; }
    public DateTime Date { get; init; }
    public string CtNum { get; init; } = "";
    public string DocumentReference { get; init; } = "";

    public string ArticleReference { get; init; } = "";
    public string Designation { get; init; } = "";
    public decimal Quantite { get; init; }
    public decimal PrixUnitaire { get; init; }
    /// <summary>Unité de vente (EU_Enumere) : KG, CARTON, SAC…</summary>
    public string Unite { get; init; } = "";
    public decimal QuantiteUnite { get; init; }

    public decimal Remise1 { get; init; }
    public decimal Remise2 { get; init; }
    public decimal Remise3 { get; init; }

    public decimal Taxe1 { get; init; }
    public string CodeTaxe1 { get; init; } = "";
    public short TypeTaux1 { get; init; }
    public short TypeTaxe1 { get; init; }

    public decimal Taxe2 { get; init; }
    public string CodeTaxe2 { get; init; } = "";
    public short TypeTaux2 { get; init; }
    public short TypeTaxe2 { get; init; }

    public decimal Taxe3 { get; init; }
    public string CodeTaxe3 { get; init; } = "";

    public decimal MontantHT { get; init; }
    public decimal MontantTTC { get; init; }
    public decimal PrixUnitaireTTC { get; init; }
    public bool EstTTC { get; init; }
    public short DocType { get; init; }

    /// <summary>Les trois emplacements de taxe, dans l'ordre de Sage.</summary>
    public IEnumerable<SageTax> Taxes()
    {
        yield return new SageTax(1, CodeTaxe1, Taxe1);
        yield return new SageTax(2, CodeTaxe2, Taxe2);
        yield return new SageTax(3, CodeTaxe3, Taxe3);
    }
}
