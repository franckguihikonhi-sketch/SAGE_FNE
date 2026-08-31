using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using SageFne.Reader.Batch;
using SageFne.Reader.Certification;
using SageFne.Reader.Configuration;
using SageFne.Reader.Data;
using SageFne.Reader.Mapping;
using SageFne.Reader.Models.Fne;
using SageFne.Reader.Models.Sage;
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

// Diagnostic : inventaire des types de documents du domaine des ventes. Aucune
// conversion, aucun registre, rien d'écrit — deux SELECT et un tableau.
if (ligneDeCommande.Verbe == Verbe.TypesDocuments)
{
    var depot = hote.Services.GetRequiredService<ISageInvoiceRepository>();
    var types = await depot.GetDocumentTypesAsync();

    Titre("Types de documents — F_DOCENTETE, DO_Domaine = 0 (ventes)");
    Console.WriteLine(Source(connexionConfiguree));

    if (types.Count == 0)
    {
        Console.WriteLine();
        Console.WriteLine("  Aucun document dans le domaine des ventes.");
        return 0;
    }

    Console.WriteLine();
    Console.WriteLine($"  {"DO_Type",7}  {"Libellé usuel",-24} {"Documents",9} {"Total TTC",18}  Période");
    foreach (var type in types)
    {
        Console.WriteLine(
            $"  {type.Type,7}  {Tronquer(type.LibelleUsuel, 24),-24} {type.Nombre,9} " +
            $"{Somme(type.TotalTTC),18}  {Periode(type.PremiereDate, type.DerniereDate)}");
    }

    Console.WriteLine($"  {new string('─', 90)}");
    Console.WriteLine($"  {Pluriel(types.Count, "type")}, {Pluriel(types.Sum(type => type.Nombre), "document")}.");

    foreach (var type in types.Where(type => type.Exemples.Count > 0))
    {
        Titre($"DO_Type {type.Type} — {Pluriel(type.Exemples.Count, "exemple")} sur {type.Nombre}");
        var colonneDocType = type.Exemples.Any(exemple => exemple.DocType is not null);
        Console.WriteLine(
            $"  {"DO_Piece",-14} {"DO_Date",-11} {"DO_Tiers",-16} {"DO_TotalTTC",18}" +
            (colonneDocType ? "  DO_DocType" : ""));
        foreach (var exemple in type.Exemples)
        {
            Console.WriteLine(
                $"  {Tronquer(exemple.Piece, 14),-14} {exemple.Date,-11:dd/MM/yyyy} " +
                $"{Tronquer(exemple.Tiers, 16),-16} {Somme(exemple.TotalTTC),18}" +
                (colonneDocType ? $"  {exemple.DocType?.ToString() ?? "—",10}" : ""));
        }
    }

    // La question décisive : une facture comptabilisée est-elle la même ligne
    // passée de 6 à 7, ou une seconde ligne ? Si aucun numéro ne porte les deux
    // types, c'est une modification en place et rien ne peut partir deux fois.
    var multiples = await depot.GetPiecesMultiTypesAsync();
    var factureEtComptabilisee = multiples.Where(doublon => doublon.MemeFacture).ToList();

    Titre("Un même numéro sous plusieurs types");
    if (multiples.Count == 0)
    {
        Console.WriteLine("  Aucun. Chaque numéro de pièce ne porte qu'un seul DO_Type.");
        Console.WriteLine();
        Console.WriteLine(
            "  C'est la réponse attendue : la comptabilisation fait passer DO_Type de 6 à 7\n" +
            "  sur la ligne existante. Une facture certifiée avant comptabilisation reste donc\n" +
            "  la même pièce après, et ne peut pas être envoyée deux fois.");
    }
    else
    {
        Console.WriteLine($"  {"DO_Piece",-14} {"Documents",9}  Types      DO_DocType");
        foreach (var doublon in multiples.Take(20))
        {
            Console.WriteLine(
                $"  {Tronquer(doublon.Piece, 14),-14} {doublon.Nombre,9}  " +
                $"{string.Join(", ", doublon.Types),-9}  {string.Join(", ", doublon.DocTypes)}" +
                (doublon.MemeFacture ? "   ← facture ET comptabilisée" : ""));
        }

        if (multiples.Count > 20) Console.WriteLine($"  … et {multiples.Count - 20} autres.");

        Console.WriteLine();
        Console.WriteLine(factureEtComptabilisee.Count == 0
            ? "  Aucun de ces numéros ne porte à la fois DO_Type 6 et 7 : ce sont des souches\n" +
              "  qui se croisent (un bon de livraison et une facture au même numéro), pas des\n" +
              "  factures dupliquées. Le registre inclut le type d'origine, elles ne se\n" +
              "  confondront pas."
            : $"  ATTENTION : {Pluriel(factureEtComptabilisee.Count, "numéro")} portent à la fois\n" +
              "  DO_Type 6 et DO_Type 7. La comptabilisation aurait alors dupliqué le document\n" +
              "  au lieu de le modifier, et la même facture pourrait être certifiée deux fois.\n" +
              "  Le lot refuse d'envoyer ces pièces tant que ce n'est pas éclairci.");
    }

    Console.WriteLine();
    Console.WriteLine(
        "Lecture seule : trois SELECT sur F_DOCENTETE, plus une consultation du " +
        "catalogue pour savoir si la colonne DO_DocType existe. Rien n'a été écrit.");
    return 0;
}

// Ce que les tables du dossier portent vraiment. Deux dossiers Sage n'ont pas
// forcément les mêmes colonnes : autant le demander au catalogue plutôt que de
// le découvrir par une exception au milieu d'un lot.
if (ligneDeCommande.Verbe == Verbe.Colonnes)
{
    var depot = hote.Services.GetRequiredService<ISageInvoiceRepository>();

    Titre("Colonnes des tables Sage — d'après sys.columns");
    Console.WriteLine(Source(connexionConfiguree));

    var releve = await depot.GetColonnesManquantesAsync();
    if (releve.Count == 0)
    {
        Console.WriteLine();
        Console.WriteLine("  Hors base : le jeu d'essai porte par construction tout ce qui est attendu.");
        return 0;
    }

    Console.WriteLine();
    Console.WriteLine($"  {"Table",-14} {"Colonnes",9} {"Demandées",10} {"Absentes",9}  État");
    foreach (var table in releve)
    {
        var etat = !table.Utilisable
            ? "INUTILISABLE"
            : table.Complet ? "complète" : "lisible, incomplète";
        Console.WriteLine(
            $"  {table.Table,-14} {table.Total,9} {table.Demandees,10} {table.Absentes.Count,9}  {etat}");
    }

    foreach (var table in releve.Where(table => table.Absentes.Count > 0))
    {
        Titre($"{table.Table} — colonnes attendues mais absentes");
        foreach (var colonne in table.Absentes)
        {
            var gravite = table.AbsentesIndispensables.Contains(colonne)
                ? "INDISPENSABLE"
                : "facultative   ";
            Console.WriteLine($"  {gravite}  {colonne}");
        }
    }

    Console.WriteLine();
    Console.WriteLine(releve.All(table => table.Complet)
        ? "Toutes les colonnes attendues existent dans ce dossier."
        : "Les colonnes facultatives absentes sont simplement laissées de côté :\n" +
          "elles ne sont pas demandées dans le select, et la lecture continue.");

    Console.WriteLine();
    Console.WriteLine("Lecture seule : un SELECT sur sys.columns par table. Rien n'a été écrit.");
    return releve.All(table => table.Utilisable) ? 0 : 1;
}

// Relevé complet d'une pièce : ce que Sage porte, ce que FNE recevrait, et ce
// qui manque encore. Lecture seule, aucun envoi.
if (ligneDeCommande.Verbe == Verbe.Detail)
{
    if (ligneDeCommande.Query.Pieces.Count != 1)
    {
        Console.Error.WriteLine("detail attend un numéro de pièce, par exemple : detail 1219");
        return 2;
    }

    var numero = ligneDeCommande.Query.Pieces[0];
    var depot = hote.Services.GetRequiredService<ISageInvoiceRepository>();
    var reglages = hote.Services.GetRequiredService<IOptions<FneOptions>>().Value;

    Titre($"Pièce {numero} — relevé complet");
    Console.WriteLine(Source(connexionConfiguree));

    // Ce que le dossier ne porte pas se dit avant les chiffres : une colonne
    // absente prive le mapping d'une information, et il vaut mieux que ça se
    // voie que de lire un zéro sans savoir d'où il vient.
    var lacunes = (await depot.GetColonnesManquantesAsync())
        .Where(table => table.Absentes.Count > 0)
        .ToList();

    if (lacunes.Count > 0)
    {
        Titre("Colonnes absentes de ce dossier");
        foreach (var table in lacunes)
        {
            Console.WriteLine($"  {table.Table} : {string.Join(", ", table.Absentes)}");
        }

        Console.WriteLine();
        Console.WriteLine(
            "  Elles ne sont pas demandées dans le select, et valent leur défaut à la lecture.\n" +
            "  Voir « colonnes » pour le relevé complet.");
    }

    // 1. Tous les documents portant ce numéro, sans filtre de type : c'est ce
    //    qui permet de dire « ce n'est pas une facture » plutôt que « rien ».
    var portants = await depot.GetDocumentsByPieceAsync(numero);

    Titre($"Documents portant le numéro {numero}");
    if (portants.Count == 0)
    {
        Console.WriteLine($"  Aucun document au numéro {numero} dans le domaine des ventes.");
        return 1;
    }

    Console.WriteLine($"  {"DO_Type",7} {"Libellé",-24} {"DO_DocType",10} {"DO_Date",-11} {"DO_Tiers",-16} {"DO_TotalTTC",16}  Retenu");
    foreach (var document in portants)
    {
        var retenu = SageDocumentTypes.EstFacture(document.Type) ? "oui" : "non";
        Console.WriteLine(
            $"  {document.Type,7} {Tronquer(SageDocumentTypes.Libelle(document.Type), 24),-24} " +
            $"{document.DocType,10} {document.Date,-11:dd/MM/yyyy} {Tronquer(document.Tiers, 16),-16} " +
            $"{Somme(document.TotalTTC),16}  {retenu}");
    }

    foreach (var ecarte in portants.Where(document => !SageDocumentTypes.EstFacture(document.Type)))
    {
        Console.WriteLine();
        Console.WriteLine(
            $"  DO_Type {ecarte.Type} écarté — {SageDocumentTypes.RaisonExclusion(ecarte.Type)}");
    }

    var factures = portants.Where(document => SageDocumentTypes.EstFacture(document.Type)).ToList();
    if (factures.Count == 0)
    {
        Console.WriteLine();
        Console.WriteLine($"  Aucune facture au numéro {numero} : rien à certifier.");
        return 1;
    }

    if (factures.Select(facture => facture.Identite).Distinct().Count() > 1
        || factures.Count > 1)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"  ATTENTION : {factures.Count} factures portent ce numéro. Si l'une est en " +
            "DO_Type 6 et l'autre en 7, la comptabilisation a dupliqué le document au lieu " +
            "de le modifier — le lot refusera de l'envoyer.");
    }

    var entete = factures[^1];
    Console.WriteLine();
    Console.WriteLine($"  Identité retenue pour le registre : {entete.Identite}");
    Console.WriteLine(
        $"  DO_Type {entete.Type} ({SageDocumentTypes.Libelle(entete.Type)}), " +
        $"DO_DocType {entete.DocType} — " +
        (entete.EstComptabilisee
            ? "comptabilisée : c'est la même facture qu'en DO_Type 6, pas une nouvelle."
            : "non encore comptabilisée."));

    // 2. La conversion réelle, contrôles compris.
    var lecteurDetail = hote.Services.GetRequiredService<InvoiceBatchReader>();
    var releve = await lecteurDetail.ReadAsync(InvoiceQuery.Piece(numero));
    var piece = releve.Conversions.FirstOrDefault();

    if (piece is null)
    {
        Titre("Résultat");
        Constats(releve.Constats);
        return 1;
    }

    Titre("Client");
    Console.WriteLine($"  DO_Tiers        {piece.Header.Tiers}");
    Console.WriteLine($"  CT_Intitule     {piece.Customer?.Intitule ?? "— client introuvable dans F_COMPTET —"}");
    Console.WriteLine($"  CT_Identifiant  {Renseigne(piece.Customer?.Identifiant)}   (NCC)");
    Console.WriteLine($"  CT_Telephone    {Renseigne(piece.Customer?.Telephone)}");
    Console.WriteLine($"  CT_EMail        {Renseigne(piece.Customer?.Email)}");

    Titre($"Lignes — {Pluriel(piece.Lines.Count, "ligne")}");
    Console.WriteLine(
        $"  {"N°",3} {"AR_Ref",-14} {"Désignation",-24} {"Qté",10} {"PU HT",13} " +
        $"{"Remise",10} {"PU net",13} {"TVA",8} {"Code FNE",14} {"AIRSI",7} {"HT",14} {"TTC",14}");

    foreach (var ligne in piece.Lines)
    {
        var remise = RemiseMapping.Read(ligne);
        var taxes = TaxMapping.Read(ligne, new ZeroVatClassifier(reglages).Classer(ligne, piece.Customer));
        var tva = TaxMapping.TauxTva(ligne);
        var airsi = TaxMapping.TauxPrelevements(ligne);

        Console.WriteLine(
            $"  {ligne.Ligne,3} {Tronquer(ligne.ArticleReference, 14),-14} " +
            $"{Tronquer(ligne.Designation, 24),-24} {Nombre(ligne.Quantite),10} " +
            $"{Nombre(ligne.PrixUnitaire),13} {Nombre(remise.RemiseUnitaire),10} " +
            $"{Nombre(remise.PrixUnitaireNet),13} {Pourcent(tva),8} " +
            $"{CodeTaxe(taxes),14} {Pourcent(airsi),7} " +
            $"{Somme(ligne.MontantHT),14} {Somme(ligne.MontantTTC),14}");
    }

    Titre("Totaux");
    Console.WriteLine($"  Total HT calculé depuis les lignes    {Somme(piece.TotalHT),18}");
    Console.WriteLine($"  Total TTC calculé depuis les lignes   {Somme(piece.TotalTTC),18}");
    Console.WriteLine($"  DO_TotalHT (entête)                   {Somme(entete.TotalHT),18}");
    Console.WriteLine($"  DO_TotalTTC (entête)                  {Somme(entete.TotalTTC),18}");
    Console.WriteLine($"  DO_NetAPayer (entête)                 {Somme(entete.NetAPayer),18}");

    if (piece.Report.Constats.Count > 0)
    {
        Titre("Contrôles");
        Constats(piece.Report.Constats);
    }

    // 3. Ce qui manque encore pour que la DGI accepte.
    if (piece.Invoice is { } facture)
    {
        var manques = FneCompleteness.Verifier(facture, reglages.Template);

        Titre("Champs FNE obligatoires manquants");
        if (manques.Count == 0)
        {
            Console.WriteLine("  Aucun : tous les champs obligatoires sont renseignés.");
        }
        else
        {
            foreach (var manque in manques)
            {
                Console.WriteLine($"  MANQUANT  {manque.Champ}");
                Console.WriteLine($"            source : {manque.Origine}");
                Console.WriteLine($"            {manque.Consequence}");
            }
        }

        Titre("Valeurs supposées, faute de source dans Sage");
        foreach (var hypothese in FneCompleteness.Hypotheses(facture))
        {
            Console.WriteLine($"  {hypothese.Champ} — {hypothese.Origine}");
            Console.WriteLine($"    {hypothese.Consequence}");
        }

        Titre($"JSON FNE — pièce {numero}");
        Console.WriteLine(JsonSerializer.Serialize(facture, JsonFne()));
    }
    else
    {
        Titre("JSON FNE");
        Console.WriteLine("  Non produit : les contrôles ci-dessus l'empêchent.");
    }

    Console.WriteLine();
    Console.WriteLine("Lecture seule. Aucun envoi, aucune écriture dans Sage.");
    return piece.Invoice is null ? 1 : 0;
}

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
        demonstration.MarquerCertifiee(
            piece.Header.Identite, piece.Header.Piece, empreinte, DateTimeOffset.Now.AddDays(-2));
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
    $"{Pluriel(lot.ACertifier, "à certifier", "à certifier")}, " +
    $"{Pluriel(lot.DejaCertifiees, "déjà certifiée")}, " +
    $"{Pluriel(lot.ModifieesDepuis, "modifiée depuis", "modifiées depuis")}, " +
    $"{Pluriel(lot.Bloquees, "bloquée")}.");

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

var options = JsonFne();

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

/// <summary>
/// Accord en nombre. Le pluriel par défaut ajoute un « s », ce qui suffit pour
/// « pièce » ou « ligne » mais pas pour un état comme « à certifier », qui est
/// invariable, ni pour « modifiée depuis », dont seul l'adjectif s'accorde.
/// </summary>
/// <summary>D'où viennent les chiffres affichés : la base, ou le jeu d'essai.</summary>
static string Source(bool connectee) => connectee
    ? "Source : base Sage (SQL Server), en lecture seule."
    : """
      Source : jeu d'essai hors base. Aucune chaîne de connexion n'est
      renseignée : voir README, « Où renseigner la connexion SQL ». Les
      chiffres ci-dessous ne sont pas ceux du dossier HT.
      """;

static JsonSerializerOptions JsonFne() => new()
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    Converters = { new DecimalJsonConverter() },
};

/// <summary>Un champ vide se dit, il ne s'affiche pas en blanc.</summary>
static string Renseigne(string? valeur) =>
    string.IsNullOrWhiteSpace(valeur) ? "— vide —" : valeur;

static string Nombre(decimal valeur) => valeur.ToString("N2", CultureInfo.GetCultureInfo("fr-FR"));

/// <summary>
/// Le code FNE de la ligne. « NON DETERMINE » quand la TVA est à 0 % sans que
/// le régime d'exonération soit classé : c'est ce qui bloque la pièce.
/// </summary>
static string CodeTaxe(TaxMapping.Resultat taxes) =>
    taxes.RegimeZeroRequis ? "NON DETERMINE" : taxes.Taxes.Count > 0 ? taxes.Taxes[0] : "—";

static string Pourcent(decimal taux) =>
    taux == 0m ? "—" : $"{taux.ToString("0.##", CultureInfo.GetCultureInfo("fr-FR"))} %";

static string Pluriel(int nombre, string singulier, string? pluriel = null) =>
    $"{nombre} {(nombre > 1 ? pluriel ?? singulier + "s" : singulier)}";

/// <summary>Bornes de dates d'un type, ou un tiret quand le dossier n'en a pas.</summary>
static string Periode(DateTime? premiere, DateTime? derniere) =>
    premiere is null || derniere is null
        ? "—"
        : $"{premiere:dd/MM/yyyy} → {derniere:dd/MM/yyyy}";

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
