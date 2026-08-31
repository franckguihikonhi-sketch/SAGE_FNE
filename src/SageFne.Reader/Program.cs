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
using SageFne.Reader.Fne;
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

// L'API de la DGI, liée sur la même section que le reste : la clé se pose donc
// en « Fne:ApiKey », dans les secrets utilisateur et nulle part ailleurs.
var api = new FneApiOptions();
builder.Configuration.GetSection(FneOptions.Section).Bind(api);
builder.Services.AddSingleton(api);
builder.Services.AddHttpClient<FneApiClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(api.TimeoutSeconds, 5, 300));
});
builder.Services.AddSingleton<InvoiceSender>();
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

// Les deux dépôts savent aussi explorer : même instance, deux rôles.
builder.Services.AddSingleton<ISageTaxInspector>(fournisseur =>
    (ISageTaxInspector)fournisseur.GetRequiredService<ISageInvoiceRepository>());

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

// Vérification de l'accès à la plateforme. Elle regarde la configuration et
// s'arrête : aucune API n'est appelée, aucune facture n'est touchée.
if (ligneDeCommande.Verbe == Verbe.Verification)
{
    var reglagesApi = hote.Services.GetRequiredService<FneApiOptions>();
    var reglagesFne = hote.Services.GetRequiredService<IOptions<FneOptions>>().Value;

    Titre("Vérification de l'accès FNE");

    void Point(bool bon, string libelle, string detail)
    {
        Console.WriteLine($"  {(bon ? "OK    " : "MANQUE")}  {libelle,-24} {detail}");
    }

    Point(true, "Environnement", reglagesApi.Environment.ToString().ToUpperInvariant());
    Point(reglagesApi.UrlRenseignee, "Fne:BaseUrl",
        reglagesApi.UrlRenseignee ? reglagesApi.BaseUrl : "non renseignée");
    Point(reglagesApi.CleRenseignee, "Fne:ApiKey",
        reglagesApi.CleRenseignee
            ? $"présente ({reglagesApi.ApiKey.Length} caractères) — {reglagesApi.CleMasquee()}"
            : "absente des secrets utilisateur");
    Point(true, "Chemin", reglagesApi.SignPath);
    Point(true, "Authentification",
        $"{reglagesApi.AuthenticationHeader}: {reglagesApi.AuthenticationScheme} <clé>".Trim());
    Point(true, "Délai", $"{reglagesApi.TimeoutSeconds} s");

    // Identifiants du dossier auprès de la DGI. Ils ne sont pas secrets — ils
    // figurent sur chaque facture certifiée — mais leur absence bloque tout.
    var pointDeVente = reglagesFne.PointOfSale;
    var etablissement = reglagesFne.Establishment;
    var identifiantsPresents =
        !EstGabarit(pointDeVente) && !EstGabarit(etablissement);

    Point(!EstGabarit(pointDeVente), "Fne:PointOfSale",
        EstGabarit(pointDeVente) ? "non renseigné" : pointDeVente);
    Point(!EstGabarit(etablissement), "Fne:Establishment",
        EstGabarit(etablissement) ? "non renseigné" : etablissement);
    Point(true, "Fne:Template", reglagesFne.Template);
    Point(true, "Fne:PaymentMethod", $"{reglagesFne.PaymentMethod} (figé, Sage ne le porte pas)");

    if (reglagesApi.UrlRenseignee && reglagesApi.EnClair)
    {
        Console.WriteLine();
        Console.WriteLine(
            "  ATTENTION — cette adresse est en HTTP clair. La clé d'API y voyage en clair,\n" +
            "  lisible de tout équipement traversé. C'est ce que publie la DGI pour son\n" +
            "  environnement d'essai : n'y utilisez jamais une clé de production, et tenez\n" +
            "  cette clé de test pour exposée.");
    }

    if (reglagesApi.UrlRenseignee && reglagesApi.CleRenseignee)
    {
        Console.WriteLine();
        Console.WriteLine($"  Adresse de certification : {reglagesApi.AdresseSignature()}");
    }

    var refus = reglagesApi.Verifier();
    Titre("Garde-fou environnement");
    if (refus is null && reglagesApi.EstTest)
    {
        Console.WriteLine("  L'adresse figure dans la liste des plateformes d'essai autorisées.");
        Console.WriteLine($"  Autorisée(s) : {string.Join(", ", reglagesApi.AdressesAutorisees)}");

        if (!string.Equals(FneApiOptions.Normaliser(reglagesApi.BaseUrl), reglagesApi.BaseUrlEffective,
                StringComparison.Ordinal))
        {
            Console.WriteLine(
                $"  Normalisée : « {reglagesApi.BaseUrl} » → « {reglagesApi.BaseUrlEffective} »\n" +
                "  Sans le chemin, l'adresse de signature aurait été fausse.");
        }
    }
    else if (refus is null)
    {
        Console.WriteLine(
            "  ATTENTION : Fne:Environment vaut PRODUCTION. Ce qui sera certifié\n" +
            "  engagera l'entreprise et ne pourra être corrigé que par un avoir.");
    }
    else
    {
        Console.WriteLine($"  REFUS — {refus}");
    }

    if (!identifiantsPresents)
    {
        Titre("Identifiants du dossier");
        Console.WriteLine("""
              ERREUR — pointOfSale et establishment doivent être renseignés. Ils
              identifient le point de vente et l'établissement déclarés à la DGI :
              sans eux, la facture partirait rattachée à un point inconnu.

                cd src\SageFne.Reader
                dotnet user-secrets set "Fne:PointOfSale"   "…"
                dotnet user-secrets set "Fne:Establishment" "…"

              Tout envoi est bloqué tant qu'ils manquent.
              """);
    }

    Titre("Conclusion");
    if (!reglagesApi.CleRenseignee || !reglagesApi.UrlRenseignee)
    {
        Console.WriteLine("""
              Configuration incomplète. Dans les secrets utilisateur — jamais dans
              appsettings.json, qui est suivi par Git :

                cd src\SageFne.Reader
                dotnet user-secrets set "Fne:BaseUrl" "https://…test…/"
                dotnet user-secrets set "Fne:ApiKey"  "…"
              """);
    }
    else if (refus is not null)
    {
        Console.WriteLine("  L'accès est renseigné mais refusé par le garde-fou ci-dessus.");
    }
    else if (!identifiantsPresents)
    {
        Console.WriteLine("  L'accès est configuré, mais les identifiants du dossier manquent.");
    }
    else
    {
        Console.WriteLine("  L'accès est configuré. Aucune facture n'a été envoyée, aucune API appelée.");
    }

    Console.WriteLine();
    Console.WriteLine(
        "Cette commande ne contacte aucun service : elle ne fait que lire la configuration.\n" +
        "La clé n'est jamais affichée en clair, ni ici, ni dans les journaux.");

    return reglagesApi.EstConfigure && identifiantsPresents ? 0 : 1;
}

// Envoi à la certification. Par défaut la commande montre la requête et
// s'arrête : une facture certifiée ne s'annule pas, elle se corrige par un
// avoir. Seul --confirmer déclenche l'appel.
if (ligneDeCommande.Verbe == Verbe.Envoyer)
{
    if (ligneDeCommande.Query.Pieces.Count != 1)
    {
        Console.Error.WriteLine("envoyer attend un numéro de pièce, par exemple : envoyer 1052");
        return 2;
    }

    var numeroEnvoi = ligneDeCommande.Query.Pieces[0];
    var reglagesApi = hote.Services.GetRequiredService<FneApiOptions>();

    Titre($"Envoi FNE — pièce {numeroEnvoi}");
    Console.WriteLine(Source(connexionConfiguree));

    if (!connexionConfiguree)
    {
        Console.WriteLine();
        Console.WriteLine(
            "  Refus : le jeu d'essai ne représente pas votre dossier. Une facture\n" +
            "  d'essai ne s'envoie pas à la DGI. Renseignez la connexion Sage d'abord.");
        return 2;
    }

    if (!reglagesApi.EstConfigure)
    {
        Console.WriteLine();
        Console.WriteLine("""
              L'accès à la plateforme n'est pas configuré. Il faut, dans les secrets
              utilisateur — jamais dans appsettings.json, qui est suivi par Git :

                cd src\SageFne.Reader
                dotnet user-secrets set "Fne:Api:BaseUrl" "https://…"
                dotnet user-secrets set "Fne:Api:ApiKey"  "…"

              Le chemin (Fne:Api:SignPath), l'en-tête et le préfixe
              d'authentification sont paramétrables si la DGI en attend d'autres.
              """);
        return 2;
    }

    var reglagesDossier = hote.Services.GetRequiredService<IOptions<FneOptions>>().Value;
    if (EstGabarit(reglagesDossier.PointOfSale) || EstGabarit(reglagesDossier.Establishment))
    {
        Console.WriteLine();
        Console.WriteLine(
            "  Refus : Fne:PointOfSale ou Fne:Establishment n'est pas renseigné.\n" +
            "  La facture partirait rattachée à un point de vente inconnu de la DGI.\n" +
            "  Voyez « fne-check ».");
        return 2;
    }

    var expediteur = hote.Services.GetRequiredService<InvoiceSender>();
    var clientFne = hote.Services.GetRequiredService<FneApiClient>();

    // La requête exacte, avant tout appel. La clé n'est jamais affichée en clair.
    var apercuLot = await hote.Services.GetRequiredService<InvoiceBatchReader>()
        .ReadAsync(InvoiceQuery.Piece(numeroEnvoi));
    var aEnvoyer = apercuLot.Conversions.FirstOrDefault();

    if (aEnvoyer is null)
    {
        Console.WriteLine();
        Console.WriteLine($"  Aucune facture au numéro {numeroEnvoi}.");
        return 1;
    }

    Titre("État de la pièce");
    Console.WriteLine($"  Identité   {aEnvoyer.Header.Identite}");
    Console.WriteLine($"  Client     {aEnvoyer.Customer?.Intitule ?? aEnvoyer.Header.Tiers} " +
        $"(NCC {Renseigne(aEnvoyer.Customer?.Identifiant)})");
    Console.WriteLine($"  Lignes     {aEnvoyer.Lines.Count}");
    Console.WriteLine($"  Total TTC  {Somme(aEnvoyer.TotalTTC)}");
    Console.WriteLine($"  Empreinte  {aEnvoyer.Empreinte}");
    Console.WriteLine($"  État       {aEnvoyer.LibelleEtat}");

    if (aEnvoyer.Report.Constats.Count > 0)
    {
        Titre("Contrôles");
        Constats(aEnvoyer.Report.Constats);
    }

    if (aEnvoyer.Invoice is not null)
    {
        Titre("Requête qui serait envoyée");
        Console.WriteLine(clientFne.DecrireRequete(aEnvoyer.Invoice));
    }

    if (!ligneDeCommande.Confirme)
    {
        Titre("Simulation");
        Console.WriteLine(aEnvoyer.Etat == EtatPiece.ACertifier
            ? $"""
                 Rien n'a été envoyé.

                 Vérifiez la requête ci-dessus — l'adresse, l'en-tête, chaque montant.
                 Une facture certifiée ne s'annule pas : elle se corrige par un avoir.

                 Pour envoyer réellement :
                   dotnet run --project src\SageFne.Reader -- envoyer {numeroEnvoi} --confirmer
                 """
            : $"  La pièce est « {aEnvoyer.LibelleEtat} » : --confirmer serait refusé.");
        return aEnvoyer.Etat == EtatPiece.ACertifier ? 0 : 1;
    }

    Titre("Envoi réel");
    Console.WriteLine($"  POST vers {reglagesApi.AdresseSignature()}");

    var resultat = await expediteur.EnvoyerAsync(numeroEnvoi, confirme: true);

    Console.WriteLine();
    Console.WriteLine($"  État final : {resultat.Etat}");
    Console.WriteLine($"  {resultat.Message}");

    if (resultat.Reponse is { CorpsBrut: not "" } reponse)
    {
        Titre("Réponse de la plateforme");
        Console.WriteLine($"  Code HTTP : {reponse.CodeHttp?.ToString() ?? "aucune réponse"}");
        Console.WriteLine();
        Console.WriteLine(reponse.CorpsBrut);
    }

    Console.WriteLine();
    Console.WriteLine(resultat.Etat switch
    {
        EtatFne.Certified =>
            "La référence est inscrite au registre du middleware. Rien n'a été écrit dans Sage.",
        EtatFne.Sending =>
            "ATTENTION : l'issue est inconnue. La pièce reste « Sending » et ne repartira pas\n" +
            "automatiquement. Vérifiez sur le portail DGI si elle a été certifiée avant tout renvoi.",
        _ => "Rien n'a été certifié. Le registre garde la trace de la tentative.",
    });

    return resultat.Reussi ? 0 : 1;
}

// De vraies factures du dossier, fiscalement nettes, pour servir de cas d'essai
// au premier envoi. Aucune n'est envoyée : la commande les note et les classe.
if (ligneDeCommande.Verbe == Verbe.Candidats)
{
    var lecteurCandidats = hote.Services.GetRequiredService<InvoiceBatchReader>();

    Titre("Candidats au premier envoi FNE");
    Console.WriteLine(Source(connexionConfiguree));
    Console.WriteLine();
    Console.WriteLine(
        $"  Lecture de {ligneDeCommande.Query.Describe()}, limite {ligneDeCommande.Query.Limite}.");

    var examen = await lecteurCandidats.ReadAsync(ligneDeCommande.Query);
    if (examen.Total == 0)
    {
        Titre("Résultat");
        Constats(examen.Constats);
        return 1;
    }

    Console.WriteLine($"  {Pluriel(examen.Total, "pièce")} examinée(s).");

    foreach (var taux in new[] { TauxRecherche.Normal, TauxRecherche.Reduit })
    {
        var evalues = examen.Conversions
            .Select(conversion => CandidatFne.Evaluer(conversion, taux, FinancialChecks.Tolerance))
            .ToList();

        var retenus = evalues
            .Where(candidat => candidat.Retenu)
            .OrderByDescending(candidat => candidat.Score)
            .ThenBy(candidat => candidat.Conversion.Lines.Count)
            .ThenBy(candidat => candidat.Conversion.Header.Piece, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Titre($"TVA {(int)taux} % — {Pluriel(retenus.Count, "candidat")}");

        if (retenus.Count == 0)
        {
            Console.WriteLine($"  Aucune facture à {(int)taux} % ne passe tous les contrôles.");

            // Un recensement, pas un échantillon : cinq pièces prises au hasard
            // ne disent pas s'il y en a douze ou huit cents derrière.
            var portantLeTaux = evalues
                .Where(candidat => !candidat.Ecarte(Disqualification.TauxAbsent))
                .ToList();

            if (portantLeTaux.Count == 0)
            {
                Console.WriteLine($"  Aucune pièce du dossier ne porte de ligne à {(int)taux} %.");
                continue;
            }

            Console.WriteLine();
            Console.WriteLine(
                $"  Sur {Pluriel(portantLeTaux.Count, "pièce")} portant du {(int)taux} %, " +
                "voici ce qui les écarte :");
            Console.WriteLine();

            foreach (var motif in portantLeTaux
                         .SelectMany(candidat => candidat.Disqualifications)
                         .GroupBy(motif => motif.Code)
                         .OrderByDescending(groupe => groupe.Count()))
            {
                Console.WriteLine($"    {motif.Key,-26} {Pluriel(motif.Count(), "pièce"),12}");

                // ERREURS_CONTROLE est un fourre-tout : sans le détail, on ne
                // sait pas s'il faut corriger une fiche client ou une quantité.
                if (motif.Key != Disqualification.ErreursControle) continue;

                var parCode = portantLeTaux
                    .Where(candidat => candidat.Ecarte(Disqualification.ErreursControle))
                    .SelectMany(candidat => candidat.Conversion.Report.Constats
                        .Where(constat => constat.Severite == Severite.Erreur)
                        .Select(constat => constat.Code)
                        .Distinct())
                    .GroupBy(code => code)
                    .OrderByDescending(groupe => groupe.Count());

                foreach (var code in parCode)
                {
                    Console.WriteLine($"      dont {code.Key,-19} {Pluriel(code.Count(), "pièce"),12}");
                }
            }

            // Les pièces dont le NCC est renseigné sont les plus proches du but :
            // il ne leur reste qu'un défaut, et il n'est pas dans la fiche client.
            var proches = portantLeTaux
                .Where(candidat => !candidat.Ecarte(Disqualification.NccAbsent))
                .OrderBy(candidat => candidat.Disqualifications.Count)
                .ThenBy(candidat => candidat.Conversion.Lines.Count)
                .Take(8)
                .ToList();

            if (proches.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"    {Pluriel(proches.Count, "pièce")} avec NCC — les plus proches du but :");
                foreach (var proche in proches)
                {
                    Console.WriteLine(
                        $"      {proche.Conversion.Header.Piece,-10} " +
                        $"{proche.Conversion.Header.Date,-11:dd/MM/yyyy} " +
                        $"{Tronquer(proche.Conversion.Customer?.Intitule ?? proche.Conversion.Header.Tiers, 24),-24} " +
                        $"{string.Join(", ", proche.Conversion.Report.Constats
                            .Where(constat => constat.Severite == Severite.Erreur)
                            .Select(constat => constat.Code).Distinct())}");
                }
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("    Aucune de ces pièces n'a de NCC : c'est le seul mur à franchir d'abord.");
            }

            continue;
        }

        var meilleur = retenus[0];
        Console.WriteLine();
        Console.WriteLine($"  ★ MEILLEUR CANDIDAT TVA {(int)taux} % — pièce {meilleur.Conversion.Header.Piece}");
        Console.WriteLine();
        Fiche(meilleur);

        if (retenus.Count > 1)
        {
            Console.WriteLine();
            Console.WriteLine($"  Autres candidats, du meilleur au moins bon :");
            Console.WriteLine(
                $"    {"Pièce",-12} {"Date",-11} {"Client",-26} {"Lg",3} {"Total TTC",15} {"Score",6}  Statut");
            foreach (var candidat in retenus.Skip(1).Take(9))
            {
                Console.WriteLine(
                    $"    {candidat.Conversion.Header.Piece,-12} " +
                    $"{candidat.Conversion.Header.Date,-11:dd/MM/yyyy} " +
                    $"{Tronquer(candidat.Conversion.Customer?.Intitule ?? "", 26),-26} " +
                    $"{candidat.Conversion.Lines.Count,3} {Somme(candidat.Conversion.TotalTTC),15} " +
                    $"{candidat.Score,6}  {candidat.Statut}");
            }

            if (retenus.Count > 10) Console.WriteLine($"    … et {retenus.Count - 10} autres.");
        }
    }

    // Le NCC manquant écarte une facture quel que soit son taux, et il se
    // corrige dans Sage, pas ici. Autant nommer les comptes concernés.
    var sansNcc = examen.Conversions
        .Where(conversion => string.IsNullOrWhiteSpace(conversion.Customer?.Identifiant))
        .GroupBy(conversion => conversion.Header.Tiers, StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(groupe => groupe.Count())
        .ToList();

    if (sansNcc.Count > 0)
    {
        var pieces = sansNcc.Sum(groupe => groupe.Count());
        Titre("Clients sans NCC");
        Console.WriteLine(
            $"  {Pluriel(pieces, "facture")} sur {examen.Total} {(pieces > 1 ? "portent" : "porte")} " +
            "un client sans CT_Identifiant.\n" +
            $"  {Pluriel(sansNcc.Count, "compte")} {Pluriel(sansNcc.Count, "concerné").Split(' ')[1]}. " +
            "Le NCC est obligatoire en B2B :\n" +
            "  ces factures ne pourront pas être certifiées tant qu'il manque.");
        Console.WriteLine();
        // Le cumul dit combien de fiches suffisent : quelques comptes portent
        // souvent l'essentiel du volume, et c'est par eux qu'il faut commencer.
        Console.WriteLine($"  {"CT_Num",-20} {"Intitulé",-32} {"Factures",9} {"Cumul",8} {"%",7}");
        var cumul = 0;
        var rang = 0;
        foreach (var compte in sansNcc.Take(15))
        {
            cumul += compte.Count();
            rang++;
            Console.WriteLine(
                $"  {Tronquer(compte.Key, 20),-20} " +
                $"{Tronquer(compte.First().Customer?.Intitule ?? "— client introuvable —", 32),-32} " +
                $"{compte.Count(),9} {cumul,8} {Part(cumul, pieces),7}");
        }

        if (sansNcc.Count > 15) Console.WriteLine($"  … et {sansNcc.Count - 15} autres comptes.");

        // Combien de fiches pour franchir la moitié du volume ?
        var moitie = 0;
        var comptesPourMoitie = 0;
        foreach (var compte in sansNcc)
        {
            moitie += compte.Count();
            comptesPourMoitie++;
            if (moitie * 2 >= pieces) break;
        }

        Console.WriteLine();
        Console.WriteLine(
            $"  Renseigner le NCC de {Pluriel(comptesPourMoitie, "compte")} suffirait à débloquer\n" +
            $"  {moitie} des {pieces} factures concernées. C'est dans Sage que cela se corrige.");
    }

    Console.WriteLine();
    Console.WriteLine("""
        Aucune facture à 0 % de TVA n'est proposée : leur régime d'exonération
        n'est pas tranché, et une pièce d'essai ne doit soulever aucune question.

        Lecture seule. Rien n'a été envoyé, rien n'a été écrit.
        """);
    return 0;
}

// Le paramétrage fiscal du dossier, pour chercher ce qui distinguerait une
// exonération conventionnelle d'une exonération légale. Rien n'est déduit ici :
// la commande montre, elle ne conclut pas.
if (ligneDeCommande.Verbe == Verbe.Taxes)
{
    if (hote.Services.GetService<ISageTaxInspector>() is not { } inspecteur)
    {
        Console.Error.WriteLine("Cette source ne permet pas l'exploration des tables.");
        return 2;
    }

    var piece = ligneDeCommande.Query.Pieces.FirstOrDefault() ?? "1219";
    var depotTaxes = hote.Services.GetRequiredService<ISageInvoiceRepository>();

    Titre($"Paramétrage fiscal — autour de la pièce {piece}");
    Console.WriteLine(Source(connexionConfiguree));

    // 1. F_TAXE, toutes colonnes : c'est là que se trouverait un code
    //    d'exonération déjà paramétré par le dossier.
    var taxes = await inspecteur.LireTableAsync("F_TAXE");
    Titre($"F_TAXE — {Pluriel(taxes.Count, "taxe")}");
    if (taxes.Count == 0)
    {
        Console.WriteLine("  Table vide ou introuvable.");
    }
    else
    {
        foreach (var taxe in taxes)
        {
            Console.WriteLine();
            Console.WriteLine($"  ── {taxe.Cle}");
            foreach (var champ in taxe.Renseignes)
            {
                Console.WriteLine($"     {champ.Colonne,-28} {champ.Valeur}");
            }
        }

        var aZero = taxes
            .Where(taxe => Taux(taxe) == 0m && !string.IsNullOrWhiteSpace(taxe.Cle))
            .ToList();

        Console.WriteLine();
        Console.WriteLine(aZero.Count > 0
            ? $"  {Pluriel(aZero.Count, "fiche")} à taux 0 : {string.Join(", ", aZero.Select(taxe => taxe.Cle))}.\n" +
              "  Si les lignes exonérées portent ce code dans DL_CodeTaxeN, il distingue les régimes."
            : "  Aucune fiche à taux 0 dans F_TAXE : une ligne exonérée ne porte donc\n" +
              "  aucun code de taxe qui dirait de quelle exonération il s'agit.");
    }

    // 2. La pièce, ses lignes, son client, ses articles.
    var fiscalite = await inspecteur.LireFiscaliteLignesAsync(piece);
    Titre($"F_DOCLIGNE — colonnes de taxe brutes de la pièce {piece}");
    if (fiscalite.Count == 0)
    {
        Console.WriteLine($"  Aucune ligne pour la pièce {piece}.");
    }

    foreach (var ligne in fiscalite)
    {
        Console.WriteLine();
        Console.WriteLine($"  ── ligne {ligne.Cle}");
        foreach (var champ in ligne.Champs)
        {
            var valeur = string.IsNullOrWhiteSpace(champ.Valeur) ? "— vide —" : champ.Valeur;
            Console.WriteLine($"     {champ.Colonne,-28} {valeur}");
        }
    }

    var entetePiece = await depotTaxes.GetInvoiceAsync(piece);
    if (entetePiece is not null)
    {
        var fiche = await inspecteur.LireLigneAsync("F_COMPTET", "CT_Num", entetePiece.Tiers);
        Titre($"F_COMPTET — client {entetePiece.Tiers}");
        Montrer(fiche, "Client introuvable dans F_COMPTET.");
    }

    var articles = fiscalite
        .Select(ligne => ligne.Valeur("AR_Ref") ?? "")
        .Where(reference => !string.IsNullOrWhiteSpace(reference))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    foreach (var reference in articles)
    {
        var fiche = await inspecteur.LireLigneAsync("F_ARTICLE", "AR_Ref", reference);
        Titre($"F_ARTICLE — article {reference}");
        Montrer(fiche, $"Article {reference} introuvable dans F_ARTICLE.");
    }

    // 3. Le verdict, qui ne tranche que ce qui est tranchable.
    Titre("Peut-on décider automatiquement entre TVAC et TVAD ?");
    var codesExoneration = fiscalite
        .SelectMany(ligne => new[] { "DL_CodeTaxe1", "DL_CodeTaxe2", "DL_CodeTaxe3" }
            .Select(colonne => (Colonne: colonne, Code: ligne.Valeur(colonne) ?? "")))
        .Where(entree => !string.IsNullOrWhiteSpace(entree.Code))
        .Select(entree => entree.Code)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    Console.WriteLine($"  Codes de taxe portés par les lignes : " +
        (codesExoneration.Count > 0 ? string.Join(", ", codesExoneration) : "aucun"));
    Console.WriteLine();
    Console.WriteLine("""
          18 % → TVA et 9 % → TVAB se décident sur le taux seul : c'est acquis.

          0 % ne se décide pas sur le taux. Il faudrait, dans ce qui précède, une
          donnée qui sépare les deux régimes — une fiche F_TAXE à 0 % portant un
          code distinct, un champ de F_COMPTET marquant le client comme titulaire
          d'un régime, ou un champ de F_ARTICLE marquant le produit comme
          légalement exonéré.

          Cette lecture ne peut pas dire lequel de ces champs porte le sens dans
          ce dossier : c'est une question fiscale, pas technique. Regardez les
          valeurs ci-dessus avec votre comptable, puis déclarez le régime dans
          appsettings.json — ZeroVatCategoryByArticle, ZeroVatCategoryByCustomer
          ou ZeroVatCategory.
          """);

    Console.WriteLine();
    Console.WriteLine(
        $"  Tant que rien n'est déclaré : {TaxMapping.CodeRegimeInconnu}, et la facture\n" +
        "  reste bloquée. C'est le comportement voulu.");

    Console.WriteLine();
    Console.WriteLine("Lecture seule : uniquement des SELECT. Rien n'a été écrit, rien n'a été envoyé.");
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

    // Mêmes règles que le lot, pour que le relevé montre ce qui partirait.
    var politique = new ConfiguredZeroVatPolicy(reglages.ZeroVat);
    var catalogueDuReleve = new TaxCatalogue(await depot.GetTaxesAsync(), reglages.CustomTaxes);
    var famillesDuReleve = await depot.GetArticleFamiliesAsync(
        piece.Lines.Select(ligne => ligne.ArticleReference).Distinct().ToList());

    Titre($"Lignes — {Pluriel(piece.Lines.Count, "ligne")}");
    Console.WriteLine(
        $"  {"N°",3} {"AR_Ref",-14} {"Désignation",-24} {"Qté",10} {"PU HT",13} " +
        $"{"Remise",10} {"PU net",13} {"TVA",8} {"Code FNE",14} {"AIRSI",7} {"HT",14} {"TTC",14}");

    foreach (var ligne in piece.Lines)
    {
        var remise = RemiseMapping.Read(ligne);
        var decision = politique.Decider(new ZeroVatContexte(
            ligne.ArticleReference,
            famillesDuReleve.GetValueOrDefault(ligne.ArticleReference, ""),
            piece.Customer?.CtNum ?? piece.Header.Tiers));
        var taxes = TaxMapping.Read(ligne, decision.Regime, catalogueDuReleve);
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

    // D'où vient — ou ne vient pas — la classification des lignes à 0 %.
    var zero = piece.Lines
        .Where(ligne => TaxMapping.TauxTva(ligne) == 0m)
        .Select(ligne => (
            Ligne: ligne,
            Decision: politique.Decider(new ZeroVatContexte(
                ligne.ArticleReference,
                famillesDuReleve.GetValueOrDefault(ligne.ArticleReference, ""),
                piece.Customer?.CtNum ?? piece.Header.Tiers))))
        .ToList();

    if (zero.Count > 0)
    {
        Titre("Classification des lignes à 0 % de TVA");
        Console.WriteLine($"  {"Ligne",5} {"Article",-14} {"Famille",-10} {"Règle appliquée",-28} Régime");
        foreach (var (ligne, decision) in zero)
        {
            Console.WriteLine(
                $"  {ligne.Ligne,5} {Tronquer(ligne.ArticleReference, 14),-14} " +
                $"{Tronquer(famillesDuReleve.GetValueOrDefault(ligne.ArticleReference, "—"), 10),-10} " +
                $"{Tronquer(decision.Origine, 28),-28} {decision.Regime.Libelle()}");
            if (decision.Erreur is not null) Console.WriteLine($"        ERREUR : {decision.Erreur}");
        }

        Console.WriteLine();
        Console.WriteLine("""
              Ordre consulté : article, puis famille, puis client, puis dossier.
              Se déclare dans appsettings.json, section Fne:ZeroVat.
              """);
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

        // Champ par champ, avec son origine. C'est le seul moyen de vérifier
        // qu'aucune valeur n'a été inventée.
        Titre("Champs FNE et leur origine");
        Console.WriteLine($"  {"Champ",-24} {"Valeur",-32} Origine");

        void Champ(string nom, string valeur, string origine) =>
            Console.WriteLine(
                $"  {nom,-24} {Tronquer(valeur == "" ? "— vide —" : valeur, 32),-32} {origine}");

        Champ("invoiceType", facture.InvoiceType, "figé : toutes les pièces partent en vente");
        Champ("paymentMethod", facture.PaymentMethod, "paramétrage — Sage ne le porte pas");
        Champ("template", facture.Template, "paramétrage Fne:Template");
        Champ("isRne", facture.IsRne ? "true" : "false", "figé à false");
        Champ("clientNcc", facture.ClientNcc, "F_COMPTET.CT_Identifiant");
        Champ("clientCompanyName", facture.ClientCompanyName, "F_COMPTET.CT_Intitule");
        Champ("clientPhone", facture.ClientPhone, "F_COMPTET.CT_Telephone");
        Champ("clientEmail", facture.ClientEmail, "F_COMPTET.CT_EMail");
        Champ("clientSellerName", facture.ClientSellerName, "non renseigné — absent de Sage");
        Champ("pointOfSale", facture.PointOfSale, "paramétrage Fne:PointOfSale");
        Champ("establishment", facture.Establishment, "paramétrage Fne:Establishment");
        Champ("discount", Nombre(facture.Discount), "remise d'entête — non lue, toujours 0");

        for (var rang = 0; rang < facture.Items.Count; rang++)
        {
            var item = facture.Items[rang];
            Console.WriteLine();
            Champ($"items[{rang}].reference", item.Reference, "F_DOCLIGNE.AR_Ref");
            Champ($"items[{rang}].description", item.Description, "F_DOCLIGNE.DL_Design");
            Champ($"items[{rang}].quantity", Nombre(item.Quantity), "F_DOCLIGNE.DL_Qte");
            Champ($"items[{rang}].amount", Nombre(item.Amount),
                "prix unitaire HT net — déduit de DL_MontantHT / DL_Qte si remise");
            Champ($"items[{rang}].discount", Nombre(item.Discount), "remise déjà déduite du prix net");
            Champ($"items[{rang}].measurementUnit", item.MeasurementUnit, "F_DOCLIGNE.EU_Enumere");
            Champ($"items[{rang}].taxes", string.Join(", ", item.Taxes),
                "taux de DL_TaxeN — 18→TVA, 9→TVAB, 0→régime déclaré");
            Champ($"items[{rang}].customTaxes",
                string.Join(", ", item.CustomTaxes.Select(taxe => $"{taxe.Name} {taxe.Amount}")),
                "prélèvements explicitement mappés (Fne:CustomTaxes)");
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
    Console.WriteLine(
        "Lecture seule : uniquement des SELECT sur Sage. Aucune API n'a été contactée,\n" +
        "aucun POST n'a été fait, rien n'a été écrit — ni dans Sage, ni au registre.");
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

/// <summary>Vide, ou resté au gabarit livré : dans les deux cas, non renseigné.</summary>
static bool EstGabarit(string? valeur) =>
    string.IsNullOrWhiteSpace(valeur)
    || valeur.Trim().Equals("A_COMPLETER", StringComparison.OrdinalIgnoreCase)
    || valeur.Trim().Equals("A_RENSEIGNER", StringComparison.OrdinalIgnoreCase);

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

/// <summary>Le détail d'un candidat, dans l'ordre où on le vérifie.</summary>
static void Fiche(CandidatFne candidat)
{
    var conversion = candidat.Conversion;
    var entete = conversion.Header;

    void Ligne(string libelle, string valeur) => Console.WriteLine($"    {libelle,-22} {valeur}");

    Ligne("DO_Piece", entete.Piece);
    Ligne("DO_Type", $"{entete.Type} ({SageDocumentTypes.Libelle(entete.Type)})");
    Ligne("DO_DocType", entete.DocType.ToString());
    Ligne("DO_Date", $"{entete.Date:dd/MM/yyyy}");
    Ligne("DO_Tiers", entete.Tiers);
    Ligne("CT_Intitule", conversion.Customer?.Intitule ?? "—");
    Ligne("CT_Identifiant (NCC)", Renseigne(conversion.Customer?.Identifiant));
    Ligne("Nombre de lignes", conversion.Lines.Count.ToString());
    Ligne("Taux de TVA", string.Join(", ", candidat.TauxRencontres.Select(taux => $"{taux:0.##} %")));
    Ligne("Custom taxes", candidat.CustomTaxes.Count > 0
        ? string.Join(", ", candidat.CustomTaxes)
        : "aucune");
    Ligne("Total HT calculé", Somme(conversion.TotalHT));
    Ligne("Total TTC calculé", Somme(conversion.TotalTTC));
    Ligne("DO_TotalTTC", Somme(entete.TotalTTC));
    Ligne("Écart TTC", $"{Somme(candidat.EcartTTC)}");
    Ligne("Statut", $"{candidat.Statut} — {conversion.LibelleEtat} (score {candidat.Score})");

    Console.WriteLine();
    Console.WriteLine("    Pourquoi ce classement :");
    foreach (var raison in candidat.Raisons) Console.WriteLine($"      · {raison}");

    if (conversion.Report.Constats.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("    Réserves :");
        foreach (var constat in conversion.Report.Constats)
        {
            Console.WriteLine($"      [{constat.Code}] {constat.Message}");
        }
    }
}

/// <summary>Affiche une fiche, ou dit pourquoi il n'y en a pas.</summary>
static void Montrer(SageEnregistrement? fiche, string absence)
{
    if (fiche is null)
    {
        Console.WriteLine($"  {absence}");
        return;
    }

    // Les colonnes dont le nom évoque la fiscalité d'abord : ce sont celles
    // qu'on cherche. Le reste suit, pour ne rien masquer.
    var fiscaux = fiche.Fiscaux.Where(champ => champ.Renseigne).ToList();
    if (fiscaux.Count > 0)
    {
        Console.WriteLine("  Colonnes dont le nom évoque la fiscalité :");
        foreach (var champ in fiscaux) Console.WriteLine($"     {champ.Colonne,-28} {champ.Valeur}");
        Console.WriteLine();
    }

    var autres = fiche.Renseignes.Except(fiscaux).ToList();
    Console.WriteLine($"  Autres colonnes renseignées ({autres.Count}) :");
    foreach (var champ in autres) Console.WriteLine($"     {champ.Colonne,-28} {champ.Valeur}");
}

/// <summary>Le taux d'une fiche F_TAXE, quel que soit le nom de sa colonne.</summary>
static decimal Taux(SageEnregistrement taxe)
{
    var brut = taxe.Champs
        .FirstOrDefault(champ => champ.Colonne.Contains("Taux", StringComparison.OrdinalIgnoreCase))
        .Valeur;

    return decimal.TryParse(brut, NumberStyles.Any, CultureInfo.InvariantCulture, out var taux)
        || decimal.TryParse(brut, NumberStyles.Any, CultureInfo.GetCultureInfo("fr-FR"), out taux)
        ? taux
        : 0m;
}

/// <summary>Une part du total, arrondie à l'entier.</summary>
static string Part(int nombre, int total) =>
    total == 0 ? "—" : $"{(decimal)nombre * 100m / total:0} %";

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
