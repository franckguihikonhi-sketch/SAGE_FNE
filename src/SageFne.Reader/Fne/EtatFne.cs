namespace SageFne.Reader.Fne;

/// <summary>
/// Où en est une facture dans la chaîne de certification.
/// </summary>
/// <remarks>
/// Ces états vivent <b>uniquement dans le registre du middleware</b>. La base
/// Sage est en lecture seule et ne porte aucune zone pour eux : rien ici n'est
/// jamais écrit dans le dossier.
///
/// <see cref="Sending"/> mérite une attention particulière. Une facture laissée
/// dans cet état signale un envoi dont on ignore l'issue — la requête est
/// partie, la réponse s'est perdue. Elle ne doit surtout pas être renvoyée sans
/// vérification : la DGI l'a peut-être certifiée.
/// </remarks>
public enum EtatFne
{
    /// <summary>Lue dans Sage, pas encore contrôlée.</summary>
    Pending,

    /// <summary>Contrôles en cours.</summary>
    Validating,

    /// <summary>Contrôlée et traduite : elle peut partir.</summary>
    Ready,

    /// <summary>Requête partie, réponse pas encore reçue.</summary>
    Sending,

    /// <summary>Certifiée par la DGI, référence en main.</summary>
    Certified,

    /// <summary>Bloquée par un contrôle, ou refusée par la plateforme.</summary>
    Error,

    /// <summary>
    /// Parvenue au portail de la DGI, en attente du clic qui la certifiera.
    /// </summary>
    /// <remarks>
    /// Sur la plateforme d'essai, le POST dépose la facture au portail ; c'est
    /// un clic sur le portail qui la certifie et lui donne sa référence. Entre
    /// les deux, la pièce n'est ni <see cref="Sending"/> — son issue est connue,
    /// elle est arrivée — ni <see cref="Certified"/> — personne ne l'a encore
    /// certifiée.
    ///
    /// Cet état <b>bloque le renvoi</b> aussi fermement que <see cref="Sending"/> :
    /// la facture est déjà au portail, un second envoi l'y mettrait deux fois.
    ///
    /// Il ne s'atteint <b>jamais</b> automatiquement. Aucun code HTTP ne le
    /// prouve — la plateforme a répondu 500 sur les trois factures qu'elle a
    /// déposées. Seul un opérateur qui a vu la pièce au portail peut l'inscrire.
    ///
    /// Sa place en fin d'énumération n'engage rien : le registre écrit ces
    /// valeurs par leur nom. Ce qui compte, en revanche, c'est que la place zéro
    /// reste occupée par l'état le moins affirmatif — un champ absent s'y relit,
    /// quel que soit le format d'écriture, et « Pending » ne prétend rien.
    /// </remarks>
    Transmise,
}
