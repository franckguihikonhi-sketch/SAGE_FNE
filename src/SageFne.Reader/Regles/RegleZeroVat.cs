using System.Text.Json.Serialization;
using SageFne.Reader.Mapping;

namespace SageFne.Reader.Regles;

/// <summary>Sur quoi une règle s'applique.</summary>
public enum PorteeRegle
{
    /// <summary>Le régime fiscal déclaré de l'acheteur — TEE, RME.</summary>
    RegimeAcheteur,

    /// <summary>Une référence d'article.</summary>
    Article,

    /// <summary>Une famille d'article.</summary>
    Famille,

    /// <summary>Un compte client, hors régime TEE/RME.</summary>
    Client,

    /// <summary>Tout le dossier.</summary>
    Dossier,
}

/// <summary>Où en est une règle de son cycle de vie.</summary>
public enum EtatRegle
{
    /// <summary>
    /// Écrite, pas validée. Elle ne produit aucun code : la pièce reste bloquée.
    /// </summary>
    /// <remarks>
    /// L'état par défaut, et le seul sûr. Une règle qui produirait un code sans
    /// avoir été validée ferait certifier des factures sous une qualification
    /// fiscale que personne n'a arrêtée.
    /// </remarks>
    Brouillon,

    /// <summary>Validée sur une preuve. Elle produit son code.</summary>
    Validee,

    /// <summary>Révoquée. Elle ne produit plus rien, et reste au registre.</summary>
    Revoquee,
}

/// <summary>
/// Une règle de classification des lignes à 0 %, avec ce qui la fonde.
/// </summary>
/// <remarks>
/// Les dictionnaires de <c>appsettings.json</c> disent quel code envoyer. Ils ne
/// savent pas répondre, six mois plus tard, à « qui a autorisé cela, sur quel
/// document, et à partir de quand ». Une facture certifiée l'est pour toujours :
/// la question se posera.
///
/// Une règle porte donc son code, son fondement, sa preuve et son état. Elle est
/// <b>versionnée</b> : une modification crée une version, elle n'écrase pas
/// celle qui a servi à certifier des factures.
/// </remarks>
public sealed record RegleZeroVat
{
    /// <summary>Identifiant stable, conservé d'une version à l'autre.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>1 pour la première écriture, puis incrémentée.</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("portee")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required PorteeRegle Portee { get; init; }

    /// <summary>
    /// Ce que la portée désigne : une référence, une famille, un compte.
    /// Vide pour <see cref="PorteeRegle.Dossier"/>.
    /// </summary>
    [JsonPropertyName("cle")]
    public string Cle { get; init; } = "";

    /// <summary>Le code FNE, et rien d'autre.</summary>
    [JsonPropertyName("code")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CodeTvaZero Code { get; init; } = CodeTvaZero.Inconnu;

    /// <summary>Pourquoi. Ne se déduit pas du code.</summary>
    [JsonPropertyName("fondement")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FondementExoneration Fondement { get; init; } = FondementExoneration.NonEtabli;

    /// <summary>
    /// Le régime déclaré de l'acheteur — <c>TEE</c> ou <c>RME</c> — pour une
    /// règle de portée <see cref="PorteeRegle.RegimeAcheteur"/>.
    /// </summary>
    [JsonPropertyName("regime")]
    public string Regime { get; init; } = "";

    [JsonPropertyName("etat")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EtatRegle Etat { get; init; } = EtatRegle.Brouillon;

    [JsonPropertyName("valideePar")]
    public string ValideePar { get; init; } = "";

    [JsonPropertyName("valideeLe")]
    public DateTimeOffset? ValideeLe { get; init; }

    /// <summary>La preuve : réponse DGI, attestation, numéro de convention.</summary>
    [JsonPropertyName("reference")]
    public string Reference { get; init; } = "";

    /// <summary>Empreinte du justificatif, quand il est conservé en fichier.</summary>
    [JsonPropertyName("empreinteJustificatif")]
    public string EmpreinteJustificatif { get; init; } = "";

    [JsonPropertyName("motif")]
    public string Motif { get; init; } = "";

    /// <summary>Bornes de validité, quand la preuve en porte.</summary>
    [JsonPropertyName("valideDu")]
    public DateTimeOffset? ValideDu { get; init; }

    [JsonPropertyName("valideAu")]
    public DateTimeOffset? ValideAu { get; init; }

    [JsonPropertyName("creeLe")]
    public DateTimeOffset CreeLe { get; init; } = DateTimeOffset.Now;

    /// <summary>Ce qui a mené à cette version, en clair.</summary>
    [JsonPropertyName("note")]
    public string Note { get; init; } = "";

    /// <summary>La clé de recherche : portée et clé, insensibles à la casse.</summary>
    [JsonIgnore]
    public string Identite => $"{Portee}/{Cle}".ToUpperInvariant();

    /// <summary>Comment cette version se nomme dans une trace d'audit.</summary>
    [JsonIgnore]
    public string Reperage => $"{Id} v{Version}";

    /// <summary>
    /// Vrai quand la règle peut produire son code à cette date.
    /// </summary>
    /// <remarks>
    /// Quatre conditions, et l'absence d'une seule bloque : validée, non
    /// révoquée, dans ses bornes, et portant un code. Une règle validée sans
    /// code ne veut rien dire — c'est une ligne restée à moitié écrite.
    /// </remarks>
    public bool Applicable(DateTimeOffset quand) =>
        Etat == EtatRegle.Validee
        && Code != CodeTvaZero.Inconnu
        && (ValideDu is null || quand >= ValideDu)
        && (ValideAu is null || quand <= ValideAu);

    /// <summary>Pourquoi elle ne s'applique pas, en clair.</summary>
    public string? Empechement(DateTimeOffset quand) => this switch
    {
        { Etat: EtatRegle.Brouillon } =>
            "elle est en brouillon : une règle ne produit son code qu'une fois validée sur une preuve",
        { Etat: EtatRegle.Revoquee } =>
            $"elle a été révoquée{(Note == "" ? "" : $" — {Note}")}",
        { Code: CodeTvaZero.Inconnu } =>
            "elle ne porte aucun code FNE",
        _ when ValideDu is { } debut && quand < debut =>
            $"elle ne prend effet que le {debut:dd/MM/yyyy}",
        _ when ValideAu is { } fin && quand > fin =>
            $"elle a cessé de valoir le {fin:dd/MM/yyyy}",
        _ => null,
    };
}
