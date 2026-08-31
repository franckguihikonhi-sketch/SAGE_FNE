namespace SageFne.Reader.Mapping;

/// <summary>
/// Le régime qui justifie une TVA à 0 % sur une ligne.
/// </summary>
/// <remarks>
/// La nomenclature FNE distingue deux exonérations que <b>rien dans Sage ne
/// sépare</b> : le taux vaut 0 dans les deux cas.
///
/// <list type="bullet">
/// <item><c>TVAC</c> — exonération conventionnelle.</item>
/// <item><c>TVAD</c> — exonération légale, TEE/RME.</item>
/// </list>
///
/// Choisir l'un ou l'autre au hasard revient à déclarer à la DGI un régime
/// fiscal qu'on ignore, sur une facture qui sera certifiée et ne pourra plus
/// être corrigée que par un avoir. <see cref="Inconnu"/> est donc la valeur par
/// défaut, et elle bloque la pièce.
/// </remarks>
public enum RegimeTvaZero
{
    /// <summary>Aucune classification : la pièce ne peut pas partir.</summary>
    Inconnu,

    /// <summary>Exonération conventionnelle. Code FNE <c>TVAC</c>.</summary>
    ExonerationConventionnelle,

    /// <summary>Exonération légale TEE/RME. Code FNE <c>TVAD</c>.</summary>
    ExonerationLegaleTeeRme,
}

public static class RegimeTvaZeroExtensions
{
    /// <summary>Le code FNE du régime, ou null quand il n'est pas déterminé.</summary>
    public static string? Code(this RegimeTvaZero regime) => regime switch
    {
        RegimeTvaZero.ExonerationConventionnelle => "TVAC",
        RegimeTvaZero.ExonerationLegaleTeeRme => "TVAD",
        _ => null,
    };

    public static string Libelle(this RegimeTvaZero regime) => regime switch
    {
        RegimeTvaZero.ExonerationConventionnelle => "exonération conventionnelle",
        RegimeTvaZero.ExonerationLegaleTeeRme => "exonération légale TEE/RME",
        _ => "non déterminé",
    };
}
