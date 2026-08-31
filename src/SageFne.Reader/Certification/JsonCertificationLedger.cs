using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SageFne.Reader.Certification;

/// <summary>
/// Registre sur fichier JSON, lisible à l'œil nu.
/// </summary>
/// <remarks>
/// L'écriture passe par un fichier temporaire renommé ensuite : une coupure
/// de courant en plein enregistrement laisse l'ancien registre intact plutôt
/// qu'un fichier tronqué. Perdre le registre reviendrait à recertifier des
/// pièces déjà envoyées à la DGI.
///
/// Un registre illisible n'arrête pas un lot : il est signalé et traité comme
/// vide, à charge pour l'exploitant de le restaurer avant d'envoyer quoi que
/// ce soit.
/// </remarks>
public sealed class JsonCertificationLedger(string chemin, ILogger<JsonCertificationLedger> logger)
    : ICertificationLedger
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly SemaphoreSlim _verrou = new(1, 1);

    /// <summary>
    /// Un registre sans chemin ne peut rien écrire : autant le dire à la
    /// construction plutôt qu'au milieu d'un envoi, là où l'échec est le plus
    /// coûteux.
    /// </summary>
    private readonly string _chemin = !string.IsNullOrWhiteSpace(chemin)
        ? chemin.Trim()
        : throw new ArgumentException(
            "Le registre des certifications n'a pas de chemin. Renseignez " +
            "Fne:CertificationLedgerPath, ou passez --registre.",
            nameof(chemin));

    public string Chemin => _chemin;

    /// <summary>Vrai quand le fichier existe mais n'a pas pu être lu.</summary>
    public bool EstIllisible { get; private set; }

    public async Task<IReadOnlyDictionary<string, CertifiedInvoice>> LookupAsync(
        IReadOnlyCollection<string> identites,
        CancellationToken cancellation = default)
    {
        var toutes = await LireAsync(cancellation);
        return identites
            .Where(toutes.ContainsKey)
            .ToDictionary(identite => identite, identite => toutes[identite], StringComparer.OrdinalIgnoreCase);
    }

    public async Task RecordAsync(CertifiedInvoice certification, CancellationToken cancellation = default)
    {
        await _verrou.WaitAsync(cancellation);
        try
        {
            var toutes = await LireAsync(cancellation);
            var registre = new Dictionary<string, CertifiedInvoice>(toutes, StringComparer.OrdinalIgnoreCase)
            {
                [certification.Identite] = certification,
            };

            var dossier = Path.GetDirectoryName(Path.GetFullPath(_chemin));
            if (!string.IsNullOrEmpty(dossier)) Directory.CreateDirectory(dossier);

            // Écriture puis remplacement : jamais de registre à moitié écrit.
            var provisoire = $"{_chemin}.tmp";
            await File.WriteAllTextAsync(
                provisoire,
                JsonSerializer.Serialize(registre.Values.OrderBy(entree => entree.Identite), Options),
                cancellation);
            File.Move(provisoire, _chemin, overwrite: true);
        }
        finally
        {
            _verrou.Release();
        }
    }

    private async Task<Dictionary<string, CertifiedInvoice>> LireAsync(CancellationToken cancellation)
    {
        if (!File.Exists(_chemin)) return new Dictionary<string, CertifiedInvoice>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var contenu = await File.ReadAllTextAsync(_chemin, cancellation);
            var entrees = JsonSerializer.Deserialize<List<CertifiedInvoice>>(contenu) ?? [];
            EstIllisible = false;
            return entrees
                .Where(entree => !string.IsNullOrWhiteSpace(entree.Identite))
                .ToDictionary(entree => entree.Identite, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception erreur) when (erreur is JsonException or IOException)
        {
            EstIllisible = true;
            logger.LogWarning(erreur, "Registre de certification illisible : {Chemin}.", chemin);
            return new Dictionary<string, CertifiedInvoice>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
