using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SageFne.Core.Configuration;
using SageFne.Core.Models.Fne;

namespace SageFne.Core.Fne;

/// <summary>
/// Envoi réel vers la plateforme de la DGI.
/// </summary>
/// <remarks>
/// La lecture de la réponse est volontairement tolérante : le format exact
/// n'est pas connu d'avance, et échouer sur un nom de champ inattendu après
/// qu'une facture a été certifiée serait le pire des cas — la DGI l'aurait
/// enregistrée, et nous l'ignorerions. Le corps brut est donc toujours
/// conservé, et la référence cherchée sous plusieurs noms plausibles.
/// </remarks>
public sealed class FneApiClient(
    HttpClient http,
    FneApiOptions options,
    ILogger<FneApiClient> logger) : IFneApiClient
{
    private static readonly JsonSerializerOptions Corps = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new DecimalJsonConverter() },
    };

    private static readonly JsonSerializerOptions Lisible = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new DecimalJsonConverter() },
    };

    /// <summary>Noms sous lesquels la référence certifiée peut se présenter.</summary>
    private static readonly string[] NomsReference =
        ["reference", "referenceFne", "invoiceReference", "referenceNumber", "numero", "fneReference"];

    private static readonly string[] NomsJeton = ["token", "qrCode", "verificationToken", "jeton"];

    public bool Reel => true;

    public string DecrireRequete(FneInvoice facture)
    {
        var entete = string.IsNullOrWhiteSpace(options.AuthenticationScheme)
            ? options.CleMasquee()
            : $"{options.AuthenticationScheme} {options.CleMasquee()}";

        return $"""
            POST {options.AdresseSignature()}
            {options.AuthenticationHeader}: {entete}
            Content-Type: application/json

            {JsonSerializer.Serialize(facture, Lisible)}
            """;
    }

    public async Task<FneSignResult> SignAsync(FneInvoice facture, CancellationToken cancellation = default)
    {
        using var requete = new HttpRequestMessage(HttpMethod.Post, options.AdresseSignature())
        {
            Content = new StringContent(
                JsonSerializer.Serialize(facture, Corps), Encoding.UTF8, "application/json"),
        };

        var valeur = string.IsNullOrWhiteSpace(options.AuthenticationScheme)
            ? options.ApiKey
            : $"{options.AuthenticationScheme} {options.ApiKey}";
        requete.Headers.TryAddWithoutValidation(options.AuthenticationHeader, valeur);
        requete.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var reponse = await http.SendAsync(requete, cancellation);
            var corps = await reponse.Content.ReadAsStringAsync(cancellation);

            if (reponse.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "FNE {Code} sur {Adresse}.", (int)reponse.StatusCode, options.AdresseSignature());
            }
            else
            {
                // Le corps de la réponse, et non le seul code. Sur le premier
                // agent en Automatic, le journal a répété « FNE 400 » pendant
                // dix minutes sans jamais dire ce que la plateforme reprochait
                // à la facture : un refus dont on ne connaît pas le motif ne se
                // corrige pas.
                //
                // Le corps d'une réponse d'erreur ne porte pas la clé — elle
                // voyage dans l'en-tête de la requête, jamais dans la réponse —
                // et il est tronqué : une page HTML d'erreur noierait le
                // journal sans rien apprendre.
                logger.LogWarning(
                    "FNE {Code} sur {Adresse} — réponse : {Corps}",
                    (int)reponse.StatusCode,
                    options.AdresseSignature(),
                    Tronquer(corps, 600));
            }

            if (!reponse.IsSuccessStatusCode)
            {
                return new FneSignResult(
                    false,
                    (int)reponse.StatusCode,
                    CorpsBrut: corps,
                    Erreur: $"la plateforme a répondu {(int)reponse.StatusCode} {reponse.ReasonPhrase}.");
            }

            var (reference, jeton) = LireReponse(corps);

            // Certifiée sans référence lisible : la facture est partie, et nous
            // ne savons pas sous quel numéro. C'est un échec à traiter à la
            // main, pas un succès.
            return reference is null
                ? new FneSignResult(
                    false,
                    (int)reponse.StatusCode,
                    Token: jeton,
                    CorpsBrut: corps,
                    Erreur: "réponse acceptée mais aucune référence n'a pu être lue. " +
                            "La facture est peut-être certifiée : vérifiez sur le portail DGI " +
                            "avant tout renvoi, et signalez le corps de réponse ci-dessus.")
                : new FneSignResult(true, (int)reponse.StatusCode, reference, jeton, corps);
        }
        catch (TaskCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new FneSignResult(
                false,
                Erreur: $"délai de {options.TimeoutSeconds} s dépassé sans réponse. " +
                        "La requête est peut-être arrivée : vérifiez sur le portail DGI avant de réessayer.");
        }
        catch (HttpRequestException erreur)
        {
            return new FneSignResult(false, Erreur: $"la plateforme est injoignable : {erreur.Message}");
        }
    }

    /// <summary>
    /// Cherche la référence et le jeton dans une réponse dont on ne connaît pas
    /// la forme, à la racine puis un niveau plus bas.
    /// </summary>
    internal static (string? Reference, string? Jeton) LireReponse(string corps)
    {
        if (string.IsNullOrWhiteSpace(corps)) return (null, null);

        try
        {
            using var document = JsonDocument.Parse(corps);
            var racine = document.RootElement;
            if (racine.ValueKind != JsonValueKind.Object) return (null, null);

            var reference = Chercher(racine, NomsReference);
            var jeton = Chercher(racine, NomsJeton);

            // Beaucoup d'API enveloppent la charge utile : on regarde un cran
            // plus bas, sans descendre plus loin pour ne pas ramasser n'importe
            // quelle chaîne au fond de l'arbre.
            if (reference is null)
            {
                foreach (var propriete in racine.EnumerateObject())
                {
                    if (propriete.Value.ValueKind != JsonValueKind.Object) continue;
                    reference ??= Chercher(propriete.Value, NomsReference);
                    jeton ??= Chercher(propriete.Value, NomsJeton);
                }
            }

            return (reference, jeton);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? Chercher(JsonElement objet, IEnumerable<string> noms)
    {
        foreach (var nom in noms)
        {
            foreach (var propriete in objet.EnumerateObject())
            {
                if (!string.Equals(propriete.Name, nom, StringComparison.OrdinalIgnoreCase)) continue;

                var valeur = propriete.Value.ValueKind switch
                {
                    JsonValueKind.String => propriete.Value.GetString(),
                    JsonValueKind.Number => propriete.Value.ToString(),
                    _ => null,
                };

                if (!string.IsNullOrWhiteSpace(valeur)) return valeur;
            }
        }

        return null;
    }

    /// <summary>Ce qu'on garde d'un corps de réponse pour le journal.</summary>
    /// <remarks>
    /// Une plateforme qui rend une page HTML d'erreur remplirait le fichier
    /// sans rien apprendre. Six cents caractères suffisent à tout message JSON
    /// utile, et la coupure se dit.
    /// </remarks>
    private static string Tronquer(string corps, int maximum)
    {
        var nu = corps.Trim();
        if (nu.Length == 0) return "— corps vide —";
        return nu.Length <= maximum ? nu : nu[..maximum] + $"… (tronqué, {nu.Length} caractères)";
    }
}
