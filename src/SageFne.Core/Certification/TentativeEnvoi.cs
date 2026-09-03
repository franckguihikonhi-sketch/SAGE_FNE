using System.Text.Json.Serialization;

namespace SageFne.Core.Certification;

/// <summary>Ce qui peut arriver à une pièce sur le chemin de la certification.</summary>
public enum GenreTentative
{
    /// <summary>Un POST est parti. Son issue n'est pas encore connue.</summary>
    Envoi,

    /// <summary>La plateforme a répondu — ou n'a pas répondu.</summary>
    Reponse,

    /// <summary>Un opérateur a tranché, portail en main.</summary>
    Decision,

    /// <summary>
    /// Un événement saisi après coup, que le middleware n'a pas observé.
    /// </summary>
    /// <remarks>
    /// Les envois antérieurs au journal n'ont laissé aucune trace : leur
    /// histoire n'a pas été perdue, elle n'a jamais été écrite. La reconstituer
    /// est légitime, la confondre avec un fait observé ne l'est pas. Ce genre
    /// existe pour que la différence reste lisible à jamais.
    /// </remarks>
    Reconstitue,

    /// <summary>Un avoir est parti pour annuler cette certification.</summary>
    /// <remarks>
    /// L'état de la pièce ne change pas : elle reste certifiée, et ne repartira
    /// pas. Un avoir n'efface pas la facture, il la contrebalance — deux
    /// documents chez la DGI, pas zéro. Le confondre avec une annulation
    /// rouvrirait la porte au renvoi, c'est-à-dire au doublon.
    /// </remarks>
    Avoir,
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

    /// <summary>Vrai quand l'événement a été saisi après coup.</summary>
    [JsonIgnore]
    public bool EstReconstitue => Genre == GenreTentative.Reconstitue;

    /// <summary>Une ligne lisible, pour l'affichage et le journal.</summary>
    public string Decrire() =>
        $"{Quand.ToLocalTime():dd/MM/yyyy HH:mm:ss}  " +
        $"{(EstReconstitue ? "~ reconstitué" : Genre.ToString()),-13} " +
        $"{(CodeHttp is { } code ? $"HTTP {code}" : "—       "),-9} {Detail}";
}
