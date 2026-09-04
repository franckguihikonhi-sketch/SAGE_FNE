using System.Text.Json;
using System.Text.Json.Serialization;
using SageFne.Core.Certification;
using SageFne.Core.Fne;

namespace SageFne.Core.Saas;

/// <summary>
/// Une ligne de la table <c>certifications</c>, telle que PostgREST l'attend.
/// </summary>
/// <remarks>
/// Les noms sont ceux des colonnes, en clair : c'est un contrat avec le schéma
/// SQL, pas une structure interne. Un renommage de colonne doit se voir ici.
///
/// Ce qui n'y figure pas est aussi délibéré que ce qui y figure : <b>aucune
/// ligne de facture, aucun montant de détail, aucune clé</b>. La base n'en veut
/// pas, et son README le dit — de quoi retrouver une facture, pas de quoi la
/// reconstituer.
/// </remarks>
public sealed record LigneMiroir
{
    [JsonPropertyName("dossier_id")] public required string DossierId { get; init; }
    [JsonPropertyName("identite")] public required string Identite { get; init; }
    [JsonPropertyName("piece")] public required string Piece { get; init; }
    [JsonPropertyName("environnement")] public required string Environnement { get; init; }

    [JsonPropertyName("etat")] public required string Etat { get; init; }
    [JsonPropertyName("empreinte")] public string Empreinte { get; init; } = "";

    [JsonPropertyName("reference_fne")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReferenceFne { get; init; }

    [JsonPropertyName("token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Token { get; init; }

    [JsonPropertyName("erreur")] public string Erreur { get; init; } = "";
    [JsonPropertyName("tentatives")] public int Tentatives { get; init; }

    [JsonPropertyName("dernier_code_http")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DernierCodeHttp { get; init; }

    [JsonPropertyName("envoyee_le")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? EnvoyeeLe { get; init; }

    [JsonPropertyName("certifiee_le")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? CertifieeLe { get; init; }

    [JsonPropertyName("reponse")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Reponse { get; init; }
}

/// <summary>
/// Traduit une entrée du registre en ligne de la base d'audit.
/// </summary>
/// <remarks>
/// Le registre fichier reste la vérité ; ceci n'en est que le reflet. La
/// traduction est donc directe, sans jugement : les états portent les mêmes
/// noms des deux côtés, et la machine à états SQL applique les mêmes trois
/// murs — <c>certified</c> terminal, <c>sending</c> et <c>transmise</c> qui ne
/// se relâchent pas.
///
/// C'est voulu qu'elle les applique. Si la base <b>refuse</b> une ligne, cela
/// veut dire que le registre local porte une transition qu'elle tient pour
/// impossible : une pièce certifiée deux fois sous deux références, par
/// exemple. Ce refus est un constat à remonter, jamais quelque chose à
/// contourner.
/// </remarks>
public static class MiroirSaas
{
    public static LigneMiroir Traduire(
        CertifiedInvoice trace, string dossierId, bool production)
    {
        var derniere = trace.DerniereReponse;

        return new LigneMiroir
        {
            DossierId = dossierId,
            Identite = trace.Identite,
            Piece = trace.Piece,
            Environnement = production ? "production" : "test",
            Etat = Etat(trace.Etat),
            Empreinte = trace.Empreinte,
            ReferenceFne = trace.ReferenceFne == "" ? null : trace.ReferenceFne,
            Token = trace.Token == "" ? null : trace.Token,
            Erreur = trace.Erreur,
            Tentatives = trace.NombreEnvois,
            DernierCodeHttp = derniere?.CodeHttp,
            EnvoyeeLe = trace.DernierEnvoi?.Quand,
            CertifieeLe = trace.Etat == EtatFne.Certified ? trace.CertifieeLe : null,
            Reponse = Corps(trace.Reponse),
        };
    }

    /// <summary>Le nom SQL de l'état. Les deux énumérations coïncident.</summary>
    private static string Etat(EtatFne etat) => etat switch
    {
        EtatFne.Pending => "pending",
        EtatFne.Validating => "validating",
        EtatFne.Ready => "ready",
        EtatFne.Sending => "sending",
        EtatFne.Certified => "certified",
        EtatFne.Transmise => "transmise",
        EtatFne.Error => "error",

        // Un état ajouté au registre et pas au schéma partirait autrement en
        // « error », c'est-à-dire en mensonge. Mieux vaut que la compilation
        // n'ait rien à dire mais que l'exécution refuse.
        _ => throw new ArgumentOutOfRangeException(
            nameof(etat), etat, "état inconnu de la base d'audit : le schéma SQL doit être étendu d'abord."),
    };

    /// <summary>
    /// La réponse de la plateforme, si elle est du JSON. Sinon rien.
    /// </summary>
    /// <remarks>
    /// La colonne est <c>jsonb</c> : y pousser une page HTML d'erreur ferait
    /// refuser toute la ligne, et perdrait l'état pour un détail. Le corps brut
    /// reste dans le registre fichier de toute façon.
    /// </remarks>
    private static JsonElement? Corps(string reponse)
    {
        if (string.IsNullOrWhiteSpace(reponse)) return null;

        try
        {
            using var document = JsonDocument.Parse(reponse);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
