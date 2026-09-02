namespace SageFne.Agent.Tableau;

/// <summary>Une réponse HTTP, construite hors de tout serveur.</summary>
/// <remarks>
/// Le routage rend cet objet et rien d'autre : aucun <c>HttpListener</c>, aucun
/// socket. C'est ce qui permet d'éprouver le tableau — y compris le bouton qui
/// certifie — sans ouvrir de port.
/// </remarks>
public sealed record ReponseHttp(int Code, string TypeContenu, string Corps)
{
    public static ReponseHttp Json(int code, string corps) =>
        new(code, "application/json; charset=utf-8", corps);

    public static ReponseHttp Html(string corps) =>
        new(200, "text/html; charset=utf-8", corps);
}

/// <summary>Un constat de contrôle, tel qu'il s'affiche.</summary>
public sealed record ConstatTableau(string Code, string Message, bool Bloquant);

/// <summary>
/// Une facture, telle que l'écran la montre.
/// </summary>
/// <remarks>
/// Rien n'est recalculé ici : chaque champ vient de la conversion et de la
/// décision que l'agent aurait prises de toute façon. Un tableau qui jugerait
/// par lui-même finirait par afficher autre chose que ce que le service fait.
/// </remarks>
public sealed record LigneTableau(
    string Piece,
    string Identite,
    string Date,
    string Client,
    string ClientNom,
    string ClientNcc,
    decimal TotalHT,
    decimal TotalTTC,
    string Etat,
    string LibelleEtat,
    string Motif,
    string Explication,
    bool Certifiable,
    string ReferenceFne,
    IReadOnlyList<ConstatTableau> Constats);

/// <summary>Ce que l'agent est en train de faire, en une réponse.</summary>
/// <param name="SurDonneesReelles">
/// Faux quand la lecture porte sur le jeu d'essai. La distinction est la
/// première chose à afficher : un écran plein de factures inventées ressemble
/// trait pour trait à un écran qui fonctionne.
/// </param>
public sealed record EtatTableau(
    string Mode,
    string Environnement,
    string BaseUrl,
    bool SurDonneesReelles,
    bool PlateformeJoignable,
    string PlateformeExplication,
    int FenetreJours,
    string DemarrageLe,
    int Total,
    int Certifiables,
    int Certifiees,
    int Bloquees,
    string Lu);

/// <summary>L'issue d'un clic sur « Certifier ».</summary>
public sealed record ResultatCertification(
    bool Reussi,
    string Piece,
    string Etat,
    string Message,
    string ReferenceFne);
