namespace SageFne.Core.Mapping;

/// <summary>
/// Le code que FNE attend dans <c>items[].taxes</c> pour une ligne à 0 %.
/// </summary>
/// <remarks>
/// <b>Un code, et rien d'autre.</b> Ce type ne dit pas pourquoi la ligne est
/// exonérée — c'est le rôle de <see cref="FondementExoneration"/>.
///
/// Les deux étaient confondus jusqu'ici sous le nom
/// <c>LegalExemptionTEE_RME</c>, qui nommait un fondement là où seul un code
/// avait sa place. La conséquence n'était pas cosmétique : déclarer un article
/// de poisson congelé sous ce nom aurait inscrit dans la configuration — donc
/// dans la piste d'audit — un régime TEE/RME que ni le produit ni le client ne
/// justifient.
///
/// La documentation d'intégration de la DGI décrit <c>TVAC</c> comme une TVA à
/// 0 % d'« exonération conventionnelle ». Le périmètre exact de <c>TVAD</c>
/// n'est pas établi par une source publique équivalente : il reste à confirmer
/// par écrit auprès de la DGI, et le middleware ne le devine pas.
/// </remarks>
public enum CodeTvaZero
{
    /// <summary>Aucune classification : la pièce ne peut pas partir.</summary>
    Inconnu,

    /// <summary>Code FNE <c>TVAC</c>.</summary>
    Tvac,

    /// <summary>Code FNE <c>TVAD</c>.</summary>
    Tvad,
}

/// <summary>
/// Pourquoi une ligne est exonérée. Ne se déduit jamais du code, ni l'inverse.
/// </summary>
/// <remarks>
/// Sert la piste d'audit, pas le mapping : le JSON envoyé à la DGI ne porte que
/// le code. Mais le jour où un contrôle demandera sur quel fondement 798
/// factures ont été certifiées, c'est ici que la réponse devra se lire.
/// </remarks>
public enum FondementExoneration
{
    /// <summary>Non établi. La règle dit quel code envoyer, pas pourquoi.</summary>
    NonEtabli,

    /// <summary>Le régime fiscal de l'acheteur — TEE, RME.</summary>
    RegimeAcheteur,

    /// <summary>Une exonération légale attachée au produit ou à l'opération.</summary>
    ExonerationLegaleProduit,

    /// <summary>Une convention, un agrément, un titre particulier.</summary>
    Convention,

    /// <summary>Un autre fondement, établi et documenté hors du middleware.</summary>
    AutreValide,
}

public static class CodeTvaZeroExtensions
{
    /// <summary>Le code FNE, ou null quand il n'est pas déterminé.</summary>
    public static string? Code(this CodeTvaZero code) => code switch
    {
        CodeTvaZero.Tvac => "TVAC",
        CodeTvaZero.Tvad => "TVAD",
        _ => null,
    };

    public static string Libelle(this CodeTvaZero code) => code switch
    {
        CodeTvaZero.Tvac => "TVAC",
        CodeTvaZero.Tvad => "TVAD",
        _ => "non déterminé",
    };

    public static string Libelle(this FondementExoneration fondement) => fondement switch
    {
        FondementExoneration.RegimeAcheteur => "régime fiscal de l'acheteur",
        FondementExoneration.ExonerationLegaleProduit => "exonération légale du produit",
        FondementExoneration.Convention => "convention ou agrément",
        FondementExoneration.AutreValide => "autre fondement documenté",
        _ => "non établi",
    };
}
