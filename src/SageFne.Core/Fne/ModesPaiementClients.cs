using System.Text.Json;

namespace SageFne.Core.Fne;

/// <summary>
/// Se souvient du mode de règlement retenu, client par client.
/// </summary>
/// <remarks>
/// <b>Une mémoire, pas une règle.</b> Le mode se choisit facture par facture,
/// au moment de certifier : c'est un fait de la transaction, et le même client
/// peut payer comptant une fois et par virement la suivante. Ce qui est retenu
/// ici ne fait que <b>présélectionner</b> le choix suivant.
///
/// Rien de tout cela ne va dans Sage, qui reste en lecture seule.
/// </remarks>
public interface IModesPaiementClients
{
    /// <summary>Le dernier mode retenu pour ce compte tiers, ou null.</summary>
    Task<string?> PourAsync(string compteTiers, CancellationToken cancellation = default);

    /// <summary>Tout ce dont on se souvient, par compte tiers.</summary>
    Task<IReadOnlyDictionary<string, string>> ToutAsync(CancellationToken cancellation = default);

    /// <summary>Retient un mode pour ce compte. Le code doit être l'un des six.</summary>
    Task RetenirAsync(string compteTiers, string code, CancellationToken cancellation = default);
}

/// <summary>Mémoire de session, pour le jeu d'essai et les tests.</summary>
public sealed class ModesPaiementMemoire : IModesPaiementClients
{
    private readonly Dictionary<string, string> _modes = new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> PourAsync(string compteTiers, CancellationToken cancellation = default) =>
        Task.FromResult(_modes.TryGetValue(compteTiers ?? "", out var mode) ? mode : null);

    public Task<IReadOnlyDictionary<string, string>> ToutAsync(CancellationToken cancellation = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(
            new Dictionary<string, string>(_modes, StringComparer.OrdinalIgnoreCase));

    public Task RetenirAsync(string compteTiers, string code, CancellationToken cancellation = default)
    {
        var normalise = ModePaiementFne.Normaliser(code)
            ?? throw new ArgumentException($"« {code} » n'est pas un mode de règlement FNE.", nameof(code));

        _modes[compteTiers] = normalise;
        return Task.CompletedTask;
    }
}

/// <summary>Mémoire sur fichier JSON, à côté du registre.</summary>
/// <remarks>
/// Écriture par fichier temporaire renommé, comme le registre : une coupure en
/// plein enregistrement laisse l'ancien fichier intact.
///
/// <b>Un fichier illisible n'arrête rien</b>, et c'est la différence avec le
/// registre des certifications. Perdre celui-ci ferait recertifier des factures
/// déjà envoyées ; perdre celui-là ne fait que reposer une question à
/// l'exploitant. La sévérité d'un défaut se règle sur ce qu'il coûte.
/// </remarks>
public sealed class ModesPaiementFichier(string chemin) : IModesPaiementClients
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };
    private readonly SemaphoreSlim _verrou = new(1, 1);

    public string Chemin { get; } = !string.IsNullOrWhiteSpace(chemin)
        ? chemin.Trim()
        : throw new ArgumentException("Chemin vide.", nameof(chemin));

    public async Task<string?> PourAsync(string compteTiers, CancellationToken cancellation = default)
    {
        var tout = await ToutAsync(cancellation);
        return tout.TryGetValue(compteTiers ?? "", out var mode) ? mode : null;
    }

    public async Task<IReadOnlyDictionary<string, string>> ToutAsync(
        CancellationToken cancellation = default)
    {
        await _verrou.WaitAsync(cancellation);
        try
        {
            return LireSansVerrou();
        }
        finally
        {
            _verrou.Release();
        }
    }

    public async Task RetenirAsync(
        string compteTiers, string code, CancellationToken cancellation = default)
    {
        var normalise = ModePaiementFne.Normaliser(code)
            ?? throw new ArgumentException($"« {code} » n'est pas un mode de règlement FNE.", nameof(code));

        if (string.IsNullOrWhiteSpace(compteTiers))
        {
            throw new ArgumentException("Compte tiers vide.", nameof(compteTiers));
        }

        await _verrou.WaitAsync(cancellation);
        try
        {
            var modes = new Dictionary<string, string>(LireSansVerrou(), StringComparer.OrdinalIgnoreCase)
            {
                [compteTiers.Trim()] = normalise,
            };

            var dossier = Path.GetDirectoryName(Chemin);
            if (!string.IsNullOrWhiteSpace(dossier)) Directory.CreateDirectory(dossier);

            var temporaire = Chemin + ".tmp";
            await File.WriteAllTextAsync(
                temporaire, JsonSerializer.Serialize(modes, Format), cancellation);
            File.Move(temporaire, Chemin, overwrite: true);
        }
        finally
        {
            _verrou.Release();
        }
    }

    private Dictionary<string, string> LireSansVerrou()
    {
        if (!File.Exists(Chemin)) return new(StringComparer.OrdinalIgnoreCase);

        try
        {
            var lu = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Chemin));
            return lu is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new(lu, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception erreur) when (erreur is JsonException or IOException)
        {
            // Voir la remarque de classe : ce fichier ne porte qu'une commodité.
            // Le perdre repose une question, il ne fait rien certifier de faux.
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
