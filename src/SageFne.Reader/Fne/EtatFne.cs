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
}
