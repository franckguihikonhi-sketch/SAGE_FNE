namespace SageFne.Reader.Mapping;

/// <summary>Ce qu'on sait d'une ligne pour classer son exonération.</summary>
/// <param name="ArticleReference">AR_Ref de la ligne.</param>
/// <param name="Famille">FA_CodeFamille de l'article, vide si non lue.</param>
/// <param name="CtNum">Compte du client de la pièce.</param>
public readonly record struct ZeroVatContexte(string ArticleReference, string Famille, string CtNum);

/// <summary>
/// Le régime retenu, et par quelle règle.
/// </summary>
/// <param name="Regime">Le régime, ou <see cref="RegimeTvaZero.Inconnu"/>.</param>
/// <param name="Origine">
/// La règle qui a décidé, en clair — « article 13415001 », « famille 02 »,
/// « dossier ». Sert au diagnostic : sans elle, on ne saurait pas pourquoi une
/// facture est partie en TVAD plutôt qu'en TVAC.
/// </param>
/// <param name="Erreur">
/// Une valeur de paramétrage refusée. Elle n'est jamais ignorée en silence :
/// une règle mal écrite doit se voir, pas se contourner.
/// </param>
public sealed record ZeroVatDecision(RegimeTvaZero Regime, string Origine, string? Erreur = null);

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
