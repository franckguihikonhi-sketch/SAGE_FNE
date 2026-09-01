using System.Text.Json.Serialization;

namespace SageFne.Reader.Certification;

/// <summary>Ce qui peut arriver à une pièce sur le chemin de la certification.</summary>
public enum GenreTentative
{
    /// <summary>Un POST est parti. Son issue n'est pas encore connue.</summary>
    Envoi,

    /// <summary>La plateforme a répondu — ou n'a pas répondu.</summary>
    Reponse,

    /// <summary>Un opérateur a tranché, portail en main.</summary>
    Decision,
}

/// <summary>
/// Une ligne du journal d'une pièce, en ajout seul.
/// </summary>
/// <remarks>
/// Ce journal est né d'un doublon réel. La pièce 1072 est partie deux fois :
/// le premier envoi a reçu un 500, l'opérateur a cherché la facture au portail
/// sans l'y trouver et l'a déclarée non certifiée, puis l'a renvoyée. Les deux
/// envois avaient en réalité créé une facture — le portail ne les publiait pas
/// encore.
///
/// Rien dans le registre n'a alerté au second envoi, parce que rien n'y
/// survivait au premier : la trace était reconstruite à neuf à chaque envoi, et
/// affirmait donc « cette pièce n'est jamais partie ». Le journal existe pour
/// que cette phrase ne puisse plus être dite à tort.
/// </remarks>
public sealed record TentativeEnvoi
{
    [JsonPropertyName("quand")]
    public required DateTimeOffset Quand { get; init; }

    [JsonPropertyName("genre")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required GenreTentative Genre { get; init; }

    /// <summary>Code HTTP, quand il y en a eu un.</summary>
    [JsonPropertyName("codeHttp")]
    public int? CodeHttp { get; init; }

    [JsonPropertyName("detail")]
    public string Detail { get; init; } = "";

    /// <summary>Une ligne lisible, pour l'affichage et le journal.</summary>
    public string Decrire() =>
        $"{Quand.ToLocalTime():dd/MM/yyyy HH:mm:ss}  " +
        $"{Genre,-9} {(CodeHttp is { } code ? $"HTTP {code}" : "—       "),-9} {Detail}";
}
