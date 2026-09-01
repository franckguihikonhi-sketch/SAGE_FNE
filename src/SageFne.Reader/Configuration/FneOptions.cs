namespace SageFne.Reader.Configuration;

/// <summary>Valeurs propres au dossier, à renseigner dans appsettings.json.</summary>
public sealed class FneOptions
{
    public const string Section = "Fne";

    public string PointOfSale { get; set; } = "";
    public string Establishment { get; set; } = "";
    /// <summary>Mode de règlement par défaut, tant que Sage ne le fournit pas.</summary>
    public string PaymentMethod { get; set; } = "deferred";
    public string Template { get; set; } = "B2B";

    /// <summary>
    /// L'entreprise émettrice relève-t-elle du Régime National de l'Entreprenant ?
    /// </summary>
    /// <remarks>
    /// Ce champ décrit <b>votre</b> régime fiscal, pas celui du client, et il
    /// part sur chaque facture certifiée. Il était figé à <c>false</c> : une
    /// valeur par défaut raisonnable, mais une déclaration fiscale ne se devine
    /// pas plus que le régime d'une exonération. Il se déclare donc, comme le
    /// reste — et <c>fne-check</c> le montre pour qu'il soit relu avant le
    /// premier envoi.
    /// </remarks>
    public bool IsRne { get; set; }

    /// <summary>
    /// Classification des lignes à 0 % de TVA. Voir <see cref="ZeroVatOptions"/>.
    /// </summary>
    public ZeroVatOptions ZeroVat { get; set; } = new();

    /// <summary>
    /// Prélèvements Sage repris en <c>customTaxes</c>, par leur code.
    /// </summary>
    /// <remarks>
    /// Un mapping <b>explicite</b>, code Sage vers nom FNE. Reprendre
    /// automatiquement tout ce qui n'est pas une TVA ferait partir à la DGI des
    /// prélèvements sous un nom que personne n'a validé. Un code absent d'ici
    /// est signalé, jamais deviné.
    /// </remarks>
    public Dictionary<string, string> CustomTaxes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase) { ["AIRSI"] = "AIRSI" };

    /// <summary>
    /// Fichier du registre des certifications. Vide : « certifications.json »
    /// à côté de l'exécutable. Ce registre ne peut pas vivre dans Sage, dont
    /// l'accès est en lecture seule.
    /// </summary>
    /// <summary>
    /// Délai minimal, en minutes, entre un envoi d'issue inconnue et le moment
    /// où l'on peut le déclarer « non certifié ».
    /// </summary>
    /// <remarks>
    /// La plateforme d'essai de la DGI a répondu 500 sur des factures qu'elle
    /// avait bel et bien enregistrées, et son portail ne les publiait pas
    /// encore. Un opérateur qui vérifie trop tôt voit une absence qui n'en est
    /// pas une : c'est ainsi qu'un doublon a été créé sur la pièce 1072.
    ///
    /// Ce délai ne garantit rien — nul ne connaît la latence réelle du portail.
    /// Il empêche seulement la vérification réflexe, faite dans la minute qui
    /// suit l'échec, quand le portail n'a encore rien à montrer.
    /// </remarks>
    public int PortalCheckDelayMinutes { get; set; } = 15;

    public string CertificationLedgerPath { get; set; } = "";
}
