using SageFne.Reader.Data;

namespace SageFne.Reader.Batch;

/// <summary>
/// Lecture des arguments du dry run.
/// </summary>
/// <remarks>
/// <c>--au</c> est inclusive pour qui la tape — « jusqu'au 31 décembre » —
/// et devient exclusive dans la requête, sans quoi les pièces datées en fin
/// de journée sortiraient du lot.
/// </remarks>
public sealed record CommandLine
{
    public InvoiceQuery Query { get; init; } = new();
    /// <summary>Dossier où écrire un fichier JSON par pièce.</summary>
    public string? Sortie { get; init; }
    /// <summary>Afficher le JSON de toutes les pièces, et pas seulement le résumé.</summary>
    public bool AfficherJson { get; init; }
    public IReadOnlyList<string> Erreurs { get; init; } = [];

    public static CommandLine Parse(string[] args)
    {
        var pieces = new List<string>();
        var erreurs = new List<string>();
        DateTime? depuis = null;
        DateTime? jusqua = null;
        var limite = 500;
        string? sortie = null;
        var afficherJson = false;

        for (var rang = 0; rang < args.Length; rang++)
        {
            var argument = args[rang];
            string? Valeur() => rang + 1 < args.Length ? args[++rang] : null;

            switch (argument)
            {
                case "--du" or "--depuis":
                    depuis = Date(Valeur(), argument, erreurs);
                    break;
                case "--au" or "--jusqua":
                    // Inclusive côté utilisateur, exclusive côté requête.
                    jusqua = Date(Valeur(), argument, erreurs)?.AddDays(1);
                    break;
                case "--limite":
                    if (int.TryParse(Valeur(), out var lu) && lu > 0) limite = lu;
                    else erreurs.Add($"{argument} attend un nombre entier positif.");
                    break;
                case "--sortie":
                    sortie = Valeur() ?? "";
                    if (sortie is "") erreurs.Add("--sortie attend un chemin de dossier.");
                    break;
                case "--json":
                    afficherJson = true;
                    break;
                default:
                    if (argument.StartsWith('-')) erreurs.Add($"Option inconnue : {argument}");
                    else pieces.Add(argument);
                    break;
            }
        }

        return new CommandLine
        {
            Query = new InvoiceQuery
            {
                Pieces = pieces,
                Depuis = depuis,
                Jusqua = jusqua,
                Limite = limite,
            },
            Sortie = sortie,
            AfficherJson = afficherJson,
            Erreurs = erreurs,
        };
    }

    private static DateTime? Date(string? valeur, string option, List<string> erreurs)
    {
        if (DateTime.TryParse(valeur, out var date)) return date.Date;
        erreurs.Add($"{option} attend une date, par exemple 2025-12-01.");
        return null;
    }

    public const string Usage = """
        Usage :
          dotnet run --project src/SageFne.Reader                        toutes les pièces, dans la limite
          dotnet run --project src/SageFne.Reader -- 1219                une pièce
          dotnet run --project src/SageFne.Reader -- 1219 1220 1221      plusieurs pièces
          dotnet run --project src/SageFne.Reader -- --du 2025-12-01 --au 2025-12-31
          dotnet run --project src/SageFne.Reader -- --du 2025-12-01 --sortie sorties/

        Options :
          --du, --au     période, bornes comprises
          --limite N     nombre maximal de pièces (500 par défaut)
          --sortie DOS   écrit un fichier JSON par pièce dans ce dossier
          --json         affiche le JSON de chaque pièce, pas seulement le résumé
        """;
}
