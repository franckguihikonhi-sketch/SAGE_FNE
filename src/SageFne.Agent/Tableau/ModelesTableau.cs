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

    /// <summary>« vente » ou « achat ». Les deux vivent dans la même liste.</summary>
    /// <remarks>
    /// Et il faut que cela se voie : une vente et un achat ne partent pas sous
    /// le même <c>invoiceType</c>, ne portent pas les mêmes règles, et n'ont
    /// pas la même conséquence fiscale. Deux listes séparées auraient évité la
    /// confusion mais aussi la vue d'ensemble, qui est ce qu'on demande à un
    /// tableau de bord.
    /// </remarks>
    string Domaine,

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

    // Ce qui partira réellement à la DGI pour cette pièce, et d'où ça vient.
    // Un mode appliqué sans être visible est un mode qu'on découvre sur la
    // facture certifiée, quand il est trop tard.
    string ModePaiement,
    string ModePaiementLibelle,
    bool ModePaiementChoisi,

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

    /// <summary>
    /// L'identifiant du binaire qui sert cette page.
    /// </summary>
    /// <remarks>
    /// La page rafraîchit ses données toutes les quinze secondes mais jamais
    /// son propre code : un onglet resté ouvert pendant une republication garde
    /// l'ancien HTML pour toujours. Deux fois de suite, une nouveauté livrée a
    /// été crue absente pour cette seule raison, et « faites Ctrl+F5 » n'est pas
    /// une réponse — c'est reporter sur l'exploitant un défaut du produit.
    ///
    /// Le numéro de version de l'assemblage ne convient pas : il vaut 1.0.0.0
    /// et ne bouge pas d'une publication à l'autre. L'identifiant de module,
    /// lui, change à chaque compilation.
    /// </remarks>
    string Build,

    // Affichés parce que leur absence est invisible partout ailleurs : ils ne
    // viennent pas de Sage, aucun contrôle de pièce ne les regarde, et la DGI
    // refuse toutes les factures quand ils manquent.
    string PointDeVente,
    string Etablissement,
    bool IdentiteRenseignee,
    int Total,
    int Certifiables,
    int Certifiees,
    int Bloquees,
    string Lu);

/// <summary>L'issue d'un clic sur « Certifier ».</summary>
/// <param name="CodeHttp">Ce que la plateforme a répondu, quand elle a répondu.</param>
/// <param name="ReponsePlateforme">
/// Le corps brut de la réponse de la DGI.
/// </param>
/// <remarks>
/// Ce corps porte le motif du refus, et il était jeté : le client d'API ne
/// garde de l'échec que la ligne de statut — « la plateforme a répondu 400 Bad
/// Request » — qui ne dit pas ce qui cloche. L'écran affichait donc un nombre
/// là où la DGI avait écrit une phrase, et il fallait aller lire le journal
/// pour la trouver.
///
/// Encore une absence prise pour une information : un code sans corps ne dit
/// pas « la facture est mauvaise », il dit « je n'ai pas regardé ».
/// </remarks>
public sealed record ResultatCertification(
    bool Reussi,
    string Piece,
    string Etat,
    string Message,
    string ReferenceFne,
    int? CodeHttp,
    string ReponsePlateforme);
