using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SageFne.Reader.Models.Fne;

/// <summary>
/// Écrit les décimaux sans leurs zéros de queue.
/// </summary>
/// <remarks>
/// Sage stocke ses montants avec six décimales : 2500,000000 et 1,500000
/// partiraient tels quels. C'est le même nombre, mais un corps de requête se
/// relit mieux avec 2500 et 1.5, et certains validateurs sont pointilleux sur
/// la forme. La valeur, elle, n'est pas touchée.
/// </remarks>
public sealed class DecimalJsonConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.GetDecimal();

    public override void Write(Utf8JsonWriter writer, decimal valeur, JsonSerializerOptions options)
    {
        writer.WriteRawValue(valeur.ToString("0.############################", CultureInfo.InvariantCulture));
    }
}
