using System.Text.Json.Serialization;

namespace SageFne.Core.Certification;

/// <summary>
/// Trace d'une pièce déjà certifiée par la DGI.
/// </summary>
/// <remarks>
/// Cette trace ne peut pas vivre dans Sage : l'accès y est en lecture seule,
/// et rien n'y prévoit de zone pour la référence FNE. Elle vit donc dans un
/// registre à nous, à côté de l'application.
///
/// L'empreinte est celle du corps de requête envoyé à la DGI. Elle permet de
/// distinguer deux situations que rien d'autre ne sépare : une pièce déjà
/// certifiée et inchangée, qu'il faut ignorer, et une pièce certifiée puis
/// modifiée dans Sage, qu'il faut signaler — la facture certifiée ne
/// correspond plus à ce que le dossier contient.
/// </remarks>
public sealed record CertifiedInvoice
{
    /// <summary>
    /// Clé du registre : domaine / type d'origine / numéro de pièce.
    /// </summary>
    /// <remarks>
    /// Le numéro seul ne peut pas servir de clé. Sage fait passer DO_Type de 6
    /// à 7 quand la facture est comptabilisée : c'est le même document, et il
    /// doit rester reconnu comme certifié. À l'inverse, un bon de livraison peut
    /// porter le même numéro qu'une facture sans être le même document.
    /// DO_DocType et DO_Piece, eux, ne bougent ni l'un ni l'autre.
    /// </remarks>
    [JsonPropertyName("identite")]
    public required string Identite { get; init; }

    /// <summary>Numéro de pièce, pour la lisibilité du registre.</summary>
    [JsonPropertyName("piece")]
    public required string Piece { get; init; }

    /// <summary>Référence certifiée renvoyée par la plateforme.</summary>
    [JsonPropertyName("referenceFne")]
    public string ReferenceFne { get; init; } = "";

    /// <summary>Jeton de vérification (QR code).</summary>
    [JsonPropertyName("token")]
    public string Token { get; init; } = "";

    [JsonPropertyName("certifieeLe")]
    public DateTimeOffset CertifieeLe { get; init; }

    /// <summary>Empreinte du corps envoyé, pour repérer une pièce modifiée depuis.</summary>
    [JsonPropertyName("empreinte")]
    public string Empreinte { get; init; } = "";

    /// <summary>
    /// Où en est la pièce dans la chaîne de certification.
    /// </summary>
    /// <remarks>
    /// Cet état vit uniquement ici. La base Sage est en lecture seule et ne
    /// porte aucune zone pour lui.
    /// </remarks>
    [JsonPropertyName("etat")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Fne.EtatFne Etat { get; init; } = Fne.EtatFne.Certified;

    /// <summary>La réponse de la plateforme, telle quelle.</summary>
    /// <remarks>
    /// Conservée pour qu'un envoi dont l'issue est douteuse puisse être
    /// instruit après coup, sans dépendre de ce que le code a su en lire.
    /// </remarks>
    [JsonPropertyName("reponse")]
    public string Reponse { get; init; } = "";

    /// <summary>Ce qui a échoué, quand l'état est <c>Error</c> ou <c>Sending</c>.</summary>
    [JsonPropertyName("erreur")]
    public string Erreur { get; init; } = "";

    /// <summary>
    /// Ce qui fonde cette ligne.
    /// </summary>
    /// <remarks>
    /// Une certification observée par le middleware et une référence relevée à
    /// la main sur le portail n'ont pas la même valeur probante : la seconde
    /// repose sur la lecture d'un humain. Les confondre rendrait tout audit
    /// impossible.
    /// </remarks>
    /// <remarks>
    /// Sans initialiseur, volontairement : le défaut est
    /// <see cref="SourceCertification.Inconnue"/>, et une entrée dépourvue de
    /// <c>source</c> doit se relire ainsi. Un initialiseur à
    /// <see cref="SourceCertification.Middleware"/> a d'abord été posé ici, et
    /// il court-circuitait la valeur zéro : les entrées anciennes se déclaraient
    /// « réponse de la DGI » sans que rien ne l'ait jamais affirmé.
    /// </remarks>
    [JsonPropertyName("source")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SourceCertification Source { get; init; }

    /// <summary>
    /// Ce qu'un humain a déclaré, et pourquoi.
    /// </summary>
    /// <remarks>
    /// Distinct d'<see cref="Erreur"/>, qui rapporte ce que la plateforme a
    /// répondu. Ici, c'est l'exploitant qui parle : constat de portail, motif
    /// d'une correction. Les corrections s'y ajoutent sans effacer les
    /// précédentes — le registre ne réécrit pas son passé.
    /// </remarks>
    [JsonPropertyName("motif")]
    public string Motif { get; init; } = "";

    /// <summary>
    /// Tout ce qui est arrivé à cette pièce, dans l'ordre.
    /// </summary>
    /// <remarks>
    /// En ajout seul, et surtout : reporté d'une écriture à l'autre. La trace
    /// était auparavant reconstruite à neuf à chaque envoi, si bien qu'un second
    /// envoi ne savait rien du premier. Un doublon réel en est né.
    /// </remarks>
    [JsonPropertyName("tentatives")]
    public IReadOnlyList<TentativeEnvoi> Tentatives { get; init; } = [];

    /// <summary>Combien de POST sont réellement partis vers la DGI.</summary>
    /// <remarks>
    /// Ne redescend jamais : un envoi parti reste parti, quoi qu'on décide
    /// ensuite de son issue.
    /// </remarks>
    [JsonIgnore]
    public int NombreEnvois => Tentatives.Count(t => t.Genre == GenreTentative.Envoi);

    /// <summary>Le dernier envoi parti, s'il y en a eu un.</summary>
    [JsonIgnore]
    public TentativeEnvoi? DernierEnvoi =>
        Tentatives.LastOrDefault(t => t.Genre == GenreTentative.Envoi);

    /// <summary>La dernière réponse reçue, s'il y en a eu une.</summary>
    [JsonIgnore]
    public TentativeEnvoi? DerniereReponse =>
        Tentatives.LastOrDefault(t => t.Genre == GenreTentative.Reponse);

    /// <summary>Ajoute une ligne au journal, sans jamais en retirer.</summary>
    /// <param name="quand">
    /// Renseigné pour un événement reconstitué, qui porte alors sa date réelle
    /// et non celle de sa saisie.
    /// </param>
    public CertifiedInvoice AvecTentative(
        GenreTentative genre, string detail, int? codeHttp = null, DateTimeOffset? quand = null) =>
        this with
        {
            Tentatives = [.. Tentatives, new TentativeEnvoi
            {
                Quand = quand ?? DateTimeOffset.Now,
                Genre = genre,
                CodeHttp = codeHttp,
                Detail = detail,
            }],
        };

    /// <summary>
    /// Le journal remis dans l'ordre des faits, pour l'affichage.
    /// </summary>
    /// <remarks>
    /// Le stockage garde l'ordre d'écriture — c'est lui, la trace. Mais un
    /// événement reconstitué porte une date passée : le lire à sa place dans la
    /// chronologie vaut mieux que de le voir surgir à la fin.
    /// </remarks>
    [JsonIgnore]
    public IReadOnlyList<TentativeEnvoi> Chronologie =>
        [.. Tentatives.OrderBy(t => t.Quand)];

    /// <summary>Vrai quand la référence est absente ou vide.</summary>
    /// <remarks>
    /// La plateforme d'essai de la DGI certifie sans toujours publier de
    /// référence exploitable : ni le PDF ni la fiche n'en portent. Une
    /// certification sans référence reste une certification, et bloque le
    /// renvoi tout autant.
    /// </remarks>
    [JsonIgnore]
    public bool SansReference => string.IsNullOrWhiteSpace(ReferenceFne);

    /// <summary>Ajoute une ligne au motif, sans effacer ce qui s'y trouve.</summary>
    public CertifiedInvoice AvecMotif(string ajout) =>
        this with { Motif = Motif == "" ? ajout : $"{Motif}\n{ajout}" };
}
