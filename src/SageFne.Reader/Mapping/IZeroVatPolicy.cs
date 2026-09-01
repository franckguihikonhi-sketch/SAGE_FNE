namespace SageFne.Reader.Mapping;

/// <summary>Ce qu'on sait d'une ligne pour classer son exonération.</summary>
/// <param name="ArticleReference">AR_Ref de la ligne.</param>
/// <param name="Famille">FA_CodeFamille de l'article, vide si non lue.</param>
/// <param name="CtNum">Compte du client de la pièce.</param>
public readonly record struct ZeroVatContexte(string ArticleReference, string Famille, string CtNum);

/// <summary>
/// Le code retenu, son fondement, et par quelle règle.
/// </summary>
/// <param name="Code">
/// Le code FNE, ou <see cref="CodeTvaZero.Inconnu"/> quand rien n'est établi.
/// Un code ne dit pas pourquoi : voir <paramref name="Fondement"/>.
/// </param>
/// <param name="Origine">
/// La règle qui a décidé, en clair — « article 13415001 », « famille 02 »,
/// « dossier ». Sert au diagnostic : sans elle, on ne saurait pas pourquoi une
/// facture est partie en TVAD plutôt qu'en TVAC.
/// </param>
/// <param name="Erreur">
/// Une valeur de paramétrage refusée. Elle n'est jamais ignorée en silence :
/// une règle mal écrite doit se voir, pas se contourner.
/// </param>
/// <param name="Fondement">
/// Pourquoi la ligne est exonérée, quand la règle le dit. Sert la piste
/// d'audit, jamais le JSON envoyé — la DGI ne reçoit qu'un code.
/// </param>
/// <param name="Avertissement">
/// Une règle acceptée mais mal formée : une valeur héritée qui nomme un
/// fondement là où seul un code a sa place, par exemple. Elle décide, et se
/// signale.
/// </param>
public sealed record ZeroVatDecision(
    CodeTvaZero Code,
    string Origine,
    string? Erreur = null,
    FondementExoneration Fondement = FondementExoneration.NonEtabli,
    string? Avertissement = null);

/// <summary>
/// D'où vient la classification des TVA à 0 %.
/// </summary>
/// <remarks>
/// Une interface, et non une classe, parce que la source des règles changera :
/// aujourd'hui appsettings.json, demain l'écran de paramétrage du SaaS. Le
/// mapping ne connaît que ce contrat.
/// </remarks>
public interface IZeroVatPolicy
{
    ZeroVatDecision Decider(ZeroVatContexte contexte);
}
