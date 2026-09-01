using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SageFne.Reader.Regles;

/// <summary>
/// Le registre des règles de TVA à 0 %, en ajout seul.
/// </summary>
/// <remarks>
/// Même philosophie que le registre des certifications, et pour la même raison :
/// une règle a servi à certifier des factures, et ces factures ne se
/// décertifient pas. Modifier une règle crée donc une <b>version</b> ; les
/// précédentes restent, et une facture peut toujours dire sous laquelle elle est
/// partie.
///
/// Ce registre ne vit pas dans <c>appsettings.json</c>. Deux raisons : le
/// binder de configuration ne sait pas lire tantôt une chaîne tantôt un objet,
/// et surtout une règle validée porte une preuve — référence, date, empreinte du
/// justificatif — qui n'a pas sa place dans un fichier qu'on édite à la main
/// entre deux déploiements.
/// </remarks>
public sealed class RegistreRegles(string chemin, ILogger<RegistreRegles> logger)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly SemaphoreSlim _verrou = new(1, 1);

    private readonly string _chemin = !string.IsNullOrWhiteSpace(chemin)
        ? chemin.Trim()
        : throw new ArgumentException(
            "Le registre des règles n'a pas de chemin.", nameof(chemin));

    public string Chemin => Path.GetFullPath(_chemin);

    /// <summary>Toutes les versions, dans l'ordre où elles ont été écrites.</summary>
    public async Task<IReadOnlyList<RegleZeroVat>> ToutAsync(CancellationToken cancellation = default)
    {
        if (!File.Exists(_chemin)) return [];

        try
        {
            var contenu = await File.ReadAllTextAsync(_chemin, cancellation);
            return JsonSerializer.Deserialize<List<RegleZeroVat>>(contenu) ?? [];
        }
        catch (Exception erreur) when (erreur is JsonException or IOException)
        {
            // Comme pour les certifications : illisible n'est pas vide. Un
            // registre vide ferait bloquer toutes les pièces, ce qui est sûr,
            // mais le dire est plus utile que le subir.
            logger.LogError("Registre des règles illisible : {Chemin} — {Cause}", _chemin, erreur.Message);
            throw new RegistreReglesIllisibleException(_chemin, erreur);
        }
    }

    /// <summary>
    /// La dernière version de chaque règle, indexée par portée et clé.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, RegleZeroVat>> CourantesAsync(
        CancellationToken cancellation = default)
    {
        var toutes = await ToutAsync(cancellation);
        return toutes
            .GroupBy(regle => regle.Identite, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                groupe => groupe.Key,
                groupe => groupe.OrderByDescending(regle => regle.Version).First(),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ajoute une version. La précédente reste, quoi qu'il arrive.
    /// </summary>
    /// <returns>La version écrite, son numéro renseigné.</returns>
    public async Task<RegleZeroVat> AjouterAsync(
        RegleZeroVat regle,
        CancellationToken cancellation = default)
    {
        await _verrou.WaitAsync(cancellation);
        try
        {
            var toutes = (await ToutAsync(cancellation)).ToList();
            var version = toutes
                .Where(existante => string.Equals(existante.Identite, regle.Identite, StringComparison.OrdinalIgnoreCase))
                .Select(existante => existante.Version)
                .DefaultIfEmpty(0)
                .Max() + 1;

            var ecrite = regle with { Version = version, CreeLe = DateTimeOffset.Now };
            toutes.Add(ecrite);

            var dossier = Path.GetDirectoryName(Path.GetFullPath(_chemin));
            if (!string.IsNullOrEmpty(dossier)) Directory.CreateDirectory(dossier);

            var provisoire = $"{_chemin}.tmp";
            await File.WriteAllTextAsync(provisoire, JsonSerializer.Serialize(toutes, Options), cancellation);
            File.Move(provisoire, _chemin, overwrite: true);

            logger.LogInformation(
                "Règle {Reperage} inscrite : {Portee} {Cle} — {Etat}",
                ecrite.Reperage, ecrite.Portee, ecrite.Cle, ecrite.Etat);

            return ecrite;
        }
        finally
        {
            _verrou.Release();
        }
    }

    /// <summary>L'historique d'une règle, de la plus ancienne version à la plus récente.</summary>
    public async Task<IReadOnlyList<RegleZeroVat>> HistoriqueAsync(
        string id,
        CancellationToken cancellation = default) =>
        [.. (await ToutAsync(cancellation))
            .Where(regle => string.Equals(regle.Id, id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(regle => regle.Version)];
}

/// <summary>Le registre des règles existe mais ne se lit pas.</summary>
public sealed class RegistreReglesIllisibleException(string chemin, Exception cause)
    : Exception(
        $"Le registre des règles de TVA à 0 % est illisible : {chemin}. " +
        "Aucune ligne à 0 % ne peut être classée tant qu'il ne l'est pas — un registre " +
        "qu'on ne sait pas lire n'est pas un registre vide.",
        cause)
{
    public string Chemin { get; } = chemin;
}
