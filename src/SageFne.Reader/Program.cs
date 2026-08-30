using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SageFne.Reader.Configuration;
using SageFne.Reader.Data;
using SageFne.Reader.Models.Fne;
using SageFne.Reader.Mapping;
using SageFne.Reader.Models.Sage;
using SageFne.Reader.Validation;

// Dry run : lire une pièce Sage, la traduire au format FNE, l'afficher.
// Rien n'est envoyé nulle part, et la base Sage n'est lue qu'en SELECT.

var piece = args.FirstOrDefault(argument => !argument.StartsWith('-')) ?? "1219";

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

using var hote = builder.Build();
var depot = hote.Services.GetRequiredService<ISageInvoiceRepository>();
var mappeur = hote.Services.GetRequiredService<IFneInvoiceMapper>();
var options = hote.Services.GetRequiredService<IOptions<FneOptions>>().Value;

Titre($"Dry run — pièce {piece}");
Console.WriteLine(connexionConfiguree
    ? "Source : base Sage (SQL Server), en lecture seule."
    : """
      Source : jeu d'essai hors base (pièce 1219 relevée dans le dossier).
      Aucune chaîne de connexion n'est renseignée — voir README, section
      « Où renseigner la connexion SQL ». Le mapping et les contrôles
      ci-dessous s'exécutent réellement.
      """);
Console.WriteLine();

var rapport = new CheckReport();

var entete = await depot.GetInvoiceAsync(piece);
SageCustomer? client = entete is null ? null : await depot.GetCustomerAsync(entete.Tiers);
var lignes = entete is null ? [] : await depot.GetInvoiceLinesAsync(piece);

InvoiceValidator.Validate(entete, client, lignes, options.Template, rapport);

if (entete is null || client is null || lignes.Count == 0)
{
    Constats(rapport);
    Console.WriteLine();
    Console.WriteLine("Lecture interrompue : il manque de quoi construire la facture.");
    return 1;
}

Titre("Ce qui a été lu dans Sage");
Console.WriteLine($"  Pièce      {entete.Piece}   type {entete.Type}, domaine {entete.Domaine}, du {entete.Date:dd/MM/yyyy}");
Console.WriteLine($"  Client     {client.CtNum}   {client.Intitule}   NCC {Ou(client.Identifiant, "absent")}");
Console.WriteLine($"  Entête     HT {entete.TotalHT}   TTC {entete.TotalTTC}   net à payer {entete.NetAPayer}");
Console.WriteLine($"  Lignes     {lignes.Count}");
foreach (var ligne in lignes)
{
    var taxes = TaxMapping.Read(ligne);
    var libelleTaxes = string.Join(" + ", taxes.Taxes.Concat(taxes.CustomTaxes.Select(t => $"{t.Name} {t.Amount} %")));
    Console.WriteLine(
        $"    {ligne.Ligne,2}. {Tronquer(ligne.Designation, 34),-34} " +
        $"{ligne.Quantite,12} {ligne.Unite,-8} x {ligne.PrixUnitaire,12} " +
        $"= {ligne.MontantHT,14} HT   {Ou(libelleTaxes, "aucune taxe")}");
}

FinancialChecks.CompareHeader(entete, lignes, rapport);
FinancialChecks.Run(lignes, rapport);

var facture = mappeur.Map(entete, lignes, client, rapport);

Titre("JSON FNE");
Console.WriteLine(JsonSerializer.Serialize(facture, new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    Converters = { new DecimalJsonConverter() },
}));

Titre("Contrôles");
Constats(rapport);

Console.WriteLine();
Console.WriteLine(rapport.ContientDesErreurs
    ? "La pièce ne peut pas partir en l'état : corrigez les erreurs ci-dessus."
    : "Aucune erreur bloquante. Rien n'a été envoyé : ce dry run s'arrête ici.");

return rapport.ContientDesErreurs ? 1 : 0;

static bool EstRenseignee(string chaine) =>
    !string.IsNullOrWhiteSpace(chaine)
    && !chaine.Contains("SERVEUR_SQL", StringComparison.OrdinalIgnoreCase)
    && !chaine.Contains("MOT_DE_PASSE", StringComparison.OrdinalIgnoreCase);

static string Ou(string valeur, string defaut) => string.IsNullOrWhiteSpace(valeur) ? defaut : valeur;

static string Tronquer(string valeur, int taille) =>
    valeur.Length <= taille ? valeur : valeur[..(taille - 1)] + "…";

static void Titre(string texte)
{
    Console.WriteLine();
    Console.WriteLine(texte);
    Console.WriteLine(new string('─', texte.Length));
}

static void Constats(CheckReport rapport)
{
    if (rapport.Constats.Count == 0)
    {
        Console.WriteLine("  Rien à signaler.");
        return;
    }

    foreach (var constat in rapport.Constats)
    {
        var marque = constat.Severite == Severite.Erreur ? "ERREUR " : "à noter";
        Console.WriteLine($"  [{marque}] {constat.Code} — {constat.Message}");
    }
}

/// <summary>Ancre pour les secrets utilisateur (dotnet user-secrets).</summary>
public partial class Program;
