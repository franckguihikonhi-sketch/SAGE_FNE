using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SageFne.Reader.Batch;
using SageFne.Reader.Certification;
using SageFne.Reader.Configuration;
using SageFne.Reader.Data;
using SageFne.Reader.Mapping;
using SageFne.Reader.Models.Fne;
using SageFne.Reader.Validation;

// Dry run : lire un lot de factures Sage, les traduire au format FNE, les
// afficher. Rien n'est envoyé nulle part, et la base Sage n'est lue qu'en SELECT.

var ligneDeCommande = CommandLine.Parse(args);
if (ligneDeCommande.Erreurs.Count > 0)
{
    foreach (var erreur in ligneDeCommande.Erreurs) Console.Error.WriteLine(erreur);
    Console.Error.WriteLine();
    Console.Error.WriteLine(CommandLine.Usage);
    return 2;
}

// La racine est le dossier de l'exécutable : appsettings.json y est copié, et
// le dry run se lance alors depuis n'importe quel répertoire.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Services.Configure<FneOptions>(builder.Configuration.GetSection(FneOptions.Section));
builder.Services.AddSingleton<IFneInvoiceMapper, FneInvoiceMapper>();
builder.Services.AddSingleton<InvoiceBatchReader>();

var chaine = builder.Configuration.GetConnectionString("Sage") ?? "";
var connexionConfiguree = EstRenseignee(chaine);

if (connexionConfiguree)
{
    builder.Services.AddSingleton<ISageInvoiceRepository>(fournisseur =>
        new SageInvoiceRepository(chaine, fournisseur.GetRequiredService<ILogger<SageInvoiceRepository>>()));
}
else
{
    builder.Services.AddSingleton<ISageInvoiceRepository, DemoSageInvoiceRepository>();
}

// Le registre des certifications vit hors de Sage : la base y est en lecture
// seule, et rien n'y prévoit de zone pour la référence FNE.
var registre = ligneDeCommande.Registre
    ?? builder.Configuration["Fne:CertificationLedgerPath"]
    ?? Path.Combine(AppContext.BaseDirectory, "certifications.json");

if (connexionConfiguree || ligneDeCommande.Registre is not null)
{
    builder.Services.AddSingleton<ICertificationLedger>(fournisseur =>
        new JsonCertificationLedger(
            registre,
            fournisseur.GetRequiredService<ILogger<JsonCertificationLedger>>()));
}
else
{
    builder.Services.AddSingleton<ICertificationLedger, DemoCertificationLedger>();
}

using var hote = builder.Build();
var lecteur = hote.Services.GetRequiredService<InvoiceBatchReader>();

// Le jeu d'essai se marque lui-même : une pièce certifiée et inchangée, une
// autre certifiée puis modifiée. Un registre vide ne montrerait ni l'un ni
// l'autre.
if (hote.Services.GetRequiredService<ICertificationLedger>() is DemoCertificationLedger demonstration)
{
    var repere = hote.Services.GetRequiredService<InvoiceBatchReader>();
    var apercu = await repere.ReadAsync(new InvoiceQuery { Pieces = ["1219", "1220"] });
    foreach (var piece in apercu.Conversions.Where(conversion => conversion.Invoice is not null))
    {
        var empreinte = piece.Header.Piece == "1220" ? "empreinte-d-avant-modification" : piece.Empreinte;
        demonstration.MarquerCertifiee(piece.Header.Piece, empreinte, DateTimeOffset.Now.AddDays(-2));
    }
}

Titre($"Dry run — {ligneDeCommande.Query.Describe()}");
Console.WriteLine(connexionConfiguree
    ? "Source : base Sage (SQL Server), en lecture seule."
    : """
      Source : jeu d'essai hors base — la pièce 1219 relevée dans le dossier,
      et trois pièces bâties autour d'elle. Aucune chaîne de connexion n'est
      renseignée : voir README, « Où renseigner la connexion SQL ». Le mapping
      et les contrôles ci-dessous s'exécutent réellement.
      """);

var lot = await lecteur.ReadAsync(ligneDeCommande.Query);

if (lot.Total == 0)
{
    Titre("Résultat");
    Constats(lot.Constats);
    return 1;
}

Titre($"Lot — {Pluriel(lot.Total, "pièce")}");
Console.WriteLine($"  {"Pièce",-10} {"Date",-11} {"Client",-30} {"Lignes",6} {"Total HT",16} {"Total TTC",16}  État");
foreach (var conversion in lot.Conversions)
{
    Console.WriteLine(
        $"  {conversion.Header.Piece,-10} {conversion.Header.Date,-11:dd/MM/yyyy} " +
        $"{Tronquer(conversion.Customer?.Intitule ?? conversion.Header.Tiers, 30),-30} " +
        $"{conversion.Lines.Count,6} {Somme(conversion.TotalHT),16} {Somme(conversion.TotalTTC),16}  " +
        $"{conversion.LibelleEtat}");
}

Console.WriteLine($"  {new string('─', 90)}");
Console.WriteLine(
    $"  {Pluriel(lot.Total, "pièce")}, {Pluriel(lot.Lignes, "ligne")}, {Somme(lot.TotalHT)} HT — " +
    $"{Pluriel(lot.ACertifier, "à certifier")}, {Pluriel(lot.DejaCertifiees, "déjà certifiée")}, " +
    $"{Pluriel(lot.ModifieesDepuis, "modifiée depuis")}, {Pluriel(lot.Bloquees, "bloquée")}.");

var constatsDuLot = lot.Constats
    .Select(constat => (Piece: "", Constat: constat))
    .Concat(lot.Conversions.SelectMany(conversion =>
        conversion.Report.Constats.Select(constat => (Piece: conversion.Header.Piece, Constat: constat))))
    .ToList();

if (constatsDuLot.Count > 0)
{
    Titre("Contrôles");
    foreach (var (piece, constat) in constatsDuLot.OrderByDescending(entree => entree.Constat.Severite == Severite.Erreur))
    {
        var marque = constat.Severite == Severite.Erreur ? "ERREUR " : "à noter";
        var ou = piece == "" ? "lot " : $"{piece,-5}";
        Console.WriteLine($"  {ou} [{marque}] {constat.Code} — {constat.Message}");
    }
}

var options = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    Converters = { new DecimalJsonConverter() },
};

// Une seule pièce : on affiche son JSON, comme à l'étape précédente. Un lot :
// seulement si on le demande, sinon la console devient illisible.
if (ligneDeCommande.AfficherJson || lot.Total == 1)
{
    foreach (var conversion in lot.Conversions.Where(APublier))
    {
        Titre($"JSON FNE — pièce {conversion.Header.Piece}");
        Console.WriteLine(JsonSerializer.Serialize(conversion.Invoice, options));
    }
}

if (ligneDeCommande.Sortie is { } dossier)
{
    Directory.CreateDirectory(dossier);
    var ecrits = 0;
    foreach (var conversion in lot.Conversions.Where(APublier))
    {
        var chemin = Path.Combine(dossier, $"{Assainir(conversion.Header.Piece)}.json");
        await File.WriteAllTextAsync(chemin, JsonSerializer.Serialize(conversion.Invoice, options));
        ecrits++;
    }

    Titre("Fichiers écrits");
    Console.WriteLine($"  {ecrits} fichier(s) dans {Path.GetFullPath(dossier)}");
}

Console.WriteLine();
Console.WriteLine(lot.Bloquees > 0 || lot.ModifieesDepuis > 0
    ? $"{Pluriel(lot.Bloquees + lot.ModifieesDepuis, "pièce")} " +
      $"ne peu{(lot.Bloquees + lot.ModifieesDepuis > 1 ? "vent" : "t")} pas partir en l'état. " +
      "Rien n'a été envoyé : ce dry run s'arrête ici."
    : "Rien ne bloque. Rien n'a été envoyé non plus : ce dry run s'arrête ici.");

return lot.Bloquees + lot.ModifieesDepuis > 0 ? 1 : 0;

/// <summary>
/// Ce qui a vocation à partir : ni les pièces bloquées, ni celles que la DGI a
/// déjà certifiées. Le lot les affiche, il ne les republie pas.
/// </summary>
static bool APublier(InvoiceConversion conversion) =>
    conversion.Invoice is not null && conversion.Etat == EtatPiece.ACertifier;

static bool EstRenseignee(string chaine) =>
    !string.IsNullOrWhiteSpace(chaine)
    && !chaine.Contains("SERVEUR_SQL", StringComparison.OrdinalIgnoreCase)
    && !chaine.Contains("MOT_DE_PASSE", StringComparison.OrdinalIgnoreCase);

static string Pluriel(int nombre, string mot) => $"{nombre} {mot}{(nombre > 1 ? "s" : "")}";

static string Somme(decimal valeur) => valeur.ToString("N2", CultureInfo.GetCultureInfo("fr-FR"));

static string Tronquer(string valeur, int taille) =>
    valeur.Length <= taille ? valeur : valeur[..(taille - 1)] + "…";

/// <summary>Un numéro de pièce peut contenir n'importe quoi ; un nom de fichier, non.</summary>
static string Assainir(string piece) =>
    string.Concat(piece.Select(caractere =>
        Path.GetInvalidFileNameChars().Contains(caractere) ? '_' : caractere));

static void Titre(string texte)
{
    Console.WriteLine();
    Console.WriteLine(texte);
    Console.WriteLine(new string('─', texte.Length));
}

static void Constats(IReadOnlyList<Constat> constats)
{
    if (constats.Count == 0)
    {
        Console.WriteLine("  Rien à signaler.");
        return;
    }

    foreach (var constat in constats)
    {
        var marque = constat.Severite == Severite.Erreur ? "ERREUR " : "à noter";
        Console.WriteLine($"  [{marque}] {constat.Code} — {constat.Message}");
    }
}

/// <summary>Ancre pour les secrets utilisateur (dotnet user-secrets).</summary>
public partial class Program;
