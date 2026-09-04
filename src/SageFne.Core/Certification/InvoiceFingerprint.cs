using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SageFne.Core.Models.Fne;

namespace SageFne.Core.Certification;

/// <summary>
/// Empreinte de ce qui partirait à la DGI.
/// </summary>
/// <remarks>
/// Calculée sur le corps de requête lui-même, et non sur les champs Sage :
/// deux pièces dont la traduction est identique doivent donner la même
/// empreinte, et une modification qui ne change rien au corps envoyé — un
/// champ Sage que le mapping n'utilise pas — ne doit pas déclencher d'alerte.
/// </remarks>
public static class InvoiceFingerprint
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new DecimalJsonConverter() },
    };

    public static string Compute(FneInvoice invoice)
    {
        var corps = JsonSerializer.Serialize(invoice, Options);
        var empreinte = SHA256.HashData(Encoding.UTF8.GetBytes(corps));
        return Convert.ToHexString(empreinte).ToLowerInvariant();
    }
}
