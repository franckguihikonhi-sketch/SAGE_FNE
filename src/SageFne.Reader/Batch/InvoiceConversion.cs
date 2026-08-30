using SageFne.Reader.Models.Fne;
using SageFne.Reader.Models.Sage;
using SageFne.Reader.Validation;

namespace SageFne.Reader.Batch;

/// <summary>
/// Ce qu'une pièce est devenue : sa facture FNE quand elle a pu être
/// construite, et dans tous les cas le rapport des contrôles.
/// </summary>
public sealed class InvoiceConversion
{
    public required SageDocumentHeader Header { get; init; }
    public SageCustomer? Customer { get; init; }
    public required IReadOnlyList<SageDocumentLine> Lines { get; init; }
    public FneInvoice? Invoice { get; init; }
    public required CheckReport Report { get; init; }

    /// <summary>Vrai quand la pièce peut partir à la certification.</summary>
    public bool EstPrete => Invoice is not null && !Report.ContientDesErreurs;

    public decimal TotalHT => Lines.Sum(ligne => ligne.MontantHT);
    public decimal TotalTTC => Lines.Sum(ligne => ligne.MontantTTC);
}

/// <summary>Le lot dans son ensemble.</summary>
public sealed class InvoiceBatch
{
    public required IReadOnlyList<InvoiceConversion> Conversions { get; init; }

    /// <summary>Constats qui portent sur le lot, pas sur une pièce.</summary>
    public required IReadOnlyList<Constat> Constats { get; init; }

    public int Total => Conversions.Count;
    public int Pretes => Conversions.Count(conversion => conversion.EstPrete);
    public int Bloquees => Total - Pretes;
    public decimal TotalHT => Conversions.Sum(conversion => conversion.TotalHT);
    public decimal TotalTTC => Conversions.Sum(conversion => conversion.TotalTTC);
    public int Lignes => Conversions.Sum(conversion => conversion.Lines.Count);
}
