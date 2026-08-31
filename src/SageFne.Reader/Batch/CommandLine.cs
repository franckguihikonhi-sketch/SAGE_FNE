using SageFne.Reader.Data;

namespace SageFne.Reader.Batch;

/// <summary>Ce que la ligne de commande demande de faire.</summary>
public enum Verbe
{
    /// <summary>Lire un lot de factures et le traduire au format FNE.</summary>
    DryRun,

    /// <summary>Inventorier les types de documents du dossier. Lecture seule.</summary>
    TypesDocuments,

    /// <summary>Relevé complet d'une pièce : Sage d'un côté, FNE de l'autre.</summary>
    Detail,

    /// <summary>Ce que les tables du dossier portent, d'après le catalogue SQL.</summary>
    Colonnes,

    /// <summary>
    /// Le paramétrage fiscal du dossier : F_TAXE, la fiche du client, celle de
    /// l'article, et les colonnes de taxe brutes d'une pièce.
    /// </summary>
    Taxes,

    /// <summary>
    /// Chercher dans le dossier de vraies factures fiscalement nettes, pour
    /// servir de cas d'essai au premier envoi.
    /// </summary>
    Candidats,
}

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
    public Verbe Verbe { get; init; } = Verbe.DryRun;
    public InvoiceQuery Query { get; init; } = new();
    /// <summary>Dossier où écrire un fichier JSON par pièce.</summary>
    public string? Sortie { get; init; }
    /// <summary>Afficher le JSON de toutes les pièces, et pas seulement le résumé.</summary>
    public bool AfficherJson { get; init; }
    /// <summary>Registre des certifications à consulter, à la place de celui configuré.</summary>
    public string? Registre { get; init; }
    public IReadOnlyList<string> Erreurs { get; init; } = [];

    public const int LimiteParDefaut = 500;

    public static CommandLine Parse(string[] args)
    {
        var pieces = new List<string>();
        var erreurs = new List<string>();
        DateTime? depuis = null;
        DateTime? jusqua = null;
        var limite = LimiteParDefaut;
        string? sortie = null;
        string? registre = null;
        var afficherJson = false;
        var verbe = Verbe.DryRun;

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
                case "--registre":
                    registre = Valeur() ?? "";
                    if (registre is "") erreurs.Add("--registre attend un chemin de fichier.");
                    break;
                case "--json":
                    afficherJson = true;
                    break;
                case "doctypes":
                    verbe = Verbe.TypesDocuments;
                    break;
                case "detail":
                    verbe = Verbe.Detail;
                    break;
                case "colonnes":
                    verbe = Verbe.Colonnes;
                    break;
                case "taxes":
                    verbe = Verbe.Taxes;
                    break;
                case "candidats-fne":
                    verbe = Verbe.Candidats;
                    // Le tri porte sur tout le dossier : la limite par défaut
                    // du dry run passerait à côté de la meilleure pièce.
                    if (limite == LimiteParDefaut) limite = 2000;
                    break;
                default:
                    if (argument.StartsWith('-')) erreurs.Add($"Option inconnue : {argument}");
                    else pieces.Add(argument);
                    break;
            }
        }

        return new CommandLine
        {
            Verbe = verbe,
            Query = new InvoiceQuery
            {
                Pieces = pieces,
                Depuis = depuis,
                Jusqua = jusqua,
                Limite = limite,
            },
            Sortie = sortie,
            Registre = registre,
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
          dotnet run --project src/SageFne.Reader -- doctypes            inventaire des types de documents
          dotnet run --project src/SageFne.Reader -- detail 1219         relevé complet d'une pièce
          dotnet run --project src/SageFne.Reader -- colonnes            colonnes réelles des tables Sage
          dotnet run --project src/SageFne.Reader -- taxes 1219          paramétrage fiscal autour d'une pièce
          dotnet run --project src/SageFne.Reader -- candidats-fne       factures d'essai fiscalement nettes

        Options :
          --du, --au     période, bornes comprises
          --limite N     nombre maximal de pièces (500 par défaut)
          --sortie DOS   écrit un fichier JSON par pièce dans ce dossier
          --registre F   registre des certifications à consulter
          --json         affiche le JSON de chaque pièce, pas seulement le résumé
        """;
}
