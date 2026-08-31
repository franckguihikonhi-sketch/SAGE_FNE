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
/// Un registre illisible arrête tout. Il fut un temps traité comme vide, ce
/// qui était l'erreur à ne pas commettre : « vide » veut dire « rien n'a jamais
/// été certifié », et un fichier tronqué aurait fait repartir vers la DGI toutes
/// les factures déjà certifiées.
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
    /// <remarks>
    /// Renseigné par <see cref="EtatDuFichierAsync"/>, qui regarde sans lever,
    /// pour que le diagnostic puisse décrire un registre que les autres
    /// opérations refusent d'utiliser.
    /// </remarks>
    public bool EstIllisible { get; private set; }

    /// <summary>
    /// Tout ce que le registre contient. Sert au diagnostic, pas au traitement.
    /// </summary>
    public async Task<IReadOnlyList<CertifiedInvoice>> ToutAsync(CancellationToken cancellation = default)
    {
        var toutes = await LireAsync(cancellation);
        return toutes.Values.OrderBy(entree => entree.Identite).ToList();
    }

    /// <summary>
    /// Ce qu'on peut dire du fichier sans exiger qu'il soit lisible.
    /// </summary>
    /// <remarks>
    /// Le diagnostic doit pouvoir décrire un registre corrompu : c'est
    /// précisément le cas où l'exploitant a besoin de savoir où il est et ce
    /// qu'il pèse. Cette méthode ne lève donc pas.
    /// </remarks>
    public async Task<RegistreFichier> EtatDuFichierAsync(CancellationToken cancellation = default)
    {
        var complet = Path.GetFullPath(_chemin);

        if (!File.Exists(complet))
        {
            EstIllisible = false;
            return new RegistreFichier(complet, Existe: false);
        }

        var fiche = new FileInfo(complet);

        try
        {
            var entrees = await ToutAsync(cancellation);
            return new RegistreFichier(
                complet, Existe: true, Octets: fiche.Length,
                ModifieLe: fiche.LastWriteTime, Entrees: entrees);
        }
        catch (RegistreIllisibleException erreur)
        {
            return new RegistreFichier(
                complet, Existe: true, Octets: fiche.Length,
                ModifieLe: fiche.LastWriteTime, Illisible: erreur.Message);
        }
    }

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
            // Ne surtout pas rendre un dictionnaire vide : l'appelant y lirait
            // « aucune pièce certifiée » et laisserait tout repartir.
            // Sans l'exception au journal : elle est relancée, et l'appelant
            // la présentera proprement. La journaliser ici ferait cracher une
            // trace de pile par-dessus le message utile.
            EstIllisible = true;
            logger.LogError("Registre de certification illisible : {Chemin} — {Cause}", _chemin, erreur.Message);
            throw new RegistreIllisibleException(_chemin, erreur);
        }
    }
}
