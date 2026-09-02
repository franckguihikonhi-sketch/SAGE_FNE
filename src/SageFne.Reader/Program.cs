using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using SageFne.Core.Audit;
using SageFne.Core.Batch;
using SageFne.Reader.Batch;
using SageFne.Core.Certification;
using SageFne.Core.Configuration;
using SageFne.Core.Data;
using SageFne.Core.Fne;
using SageFne.Core.Mapping;
using SageFne.Core.Regles;
using SageFne.Core.Models.Fne;
using SageFne.Core.Models.Sage;
using SageFne.Core.Validation;

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

var chaine = builder.Configuration.GetConnectionString("Sage") ?? "";
var connexionConfiguree = ServicesMiddleware.ConnexionRenseignee(chaine);

// Le registre vit hors de Sage. Sans connexion ni chemin explicite, il reste en
// mémoire : le jeu d'essai ne laisse pas de trace sur le disque.
var registre = ServicesMiddleware.CheminRegistre(
    ligneDeCommande.Registre,
    builder.Configuration["Fne:CertificationLedgerPath"],
    AppContext.BaseDirectory,
    connexionConfiguree);

builder.Services.AjouterMiddlewareFne(builder.Configuration, chaine, registre);

// Le conteneur est vérifié à la construction : une dépendance manquante doit
// échouer ici, pas au milieu d'un envoi.
builder.Services.Configure<ServiceProviderOptions>(options =>
{
    options.ValidateOnBuild = true;
    options.ValidateScopes = true;
});

using var hote = builder.Build();

// Un registre illisible arrête tout, et le dit une fois pour toutes plutôt que
// de laisser chaque commande buter dessus. « registre-info » reste accessible :
// c'est précisément la commande qui sert à instruire ce cas.
if (ligneDeCommande.Verbe != Verbe.RegistreInfo
    && hote.Services.GetRequiredService<ICertificationLedger>() is JsonCertificationLedger aVerifier)
{
    var sante = await aVerifier.EtatDuFichierAsync();
    if (sante.Illisible is { } empechement)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine(empechement);
        Console.Error.WriteLine();
        Console.Error.WriteLine("Pour le décrire sans l'utiliser :");
        Console.Error.WriteLine("  dotnet run --project src\\SageFne.Reader -- registre-info");
        return 1;
    }
}

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
    Point(true, "Fne:IsRne",
        $"{(reglagesFne.IsRne ? "true" : "false")} — régime de VOTRE entreprise, à relire");
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

// Où vit le registre, ce qu'il pèse, ce qu'il contient. Le diagnostic à lancer
// quand une trace attendue manque. Il ne lit pas la clé d'API.
if (ligneDeCommande.Verbe == Verbe.RegistreInfo)
{
    Titre("Registre des certifications");

    var reglagesRegistre = hote.Services.GetRequiredService<FneApiOptions>();
    Console.WriteLine($"  Environnement   {(reglagesRegistre.EstTest ? "TEST" : "PRODUCTION")}");
    Console.WriteLine($"  Base Sage       {(connexionConfiguree ? "connectée" : "jeu d'essai — aucune connexion")}");

    var tenu = hote.Services.GetRequiredService<ICertificationLedger>();

    if (tenu is not JsonCertificationLedger surFichier)
    {
        Console.WriteLine();
        Console.WriteLine("  Le registre est EN MÉMOIRE : il disparaît à la fin de la commande.");
        Console.WriteLine("  C'est le mode du jeu d'essai. Renseignez la connexion Sage, ou");
        Console.WriteLine("  Fne:CertificationLedgerPath, pour qu'il s'écrive sur le disque.");
        return 1;
    }

    var fichier = await surFichier.EtatDuFichierAsync();

    Console.WriteLine($"  Chemin          {fichier.Chemin}");
    Console.WriteLine($"  Origine         {(ligneDeCommande.Registre is not null ? "--registre" : builder.Configuration["Fne:CertificationLedgerPath"] is { Length: > 0 } ? "Fne:CertificationLedgerPath" : "défaut de l'application")}");
    Console.WriteLine($"  Fichier         {(fichier.Existe ? "présent" : "ABSENT")}");

    if (fichier.Existe)
    {
        Console.WriteLine($"  Taille          {fichier.Octets} octets");
        Console.WriteLine($"  Modifié le      {fichier.ModifieLe:dd/MM/yyyy à HH:mm:ss}");
    }

    if (fichier.Illisible is { } pourquoi)
    {
        Console.WriteLine("  Entrées         ILLISIBLE");
        Console.WriteLine();
        Console.WriteLine($"  {pourquoi}");
        return 1;
    }

    var entrees = fichier.Entrees ?? [];
    Console.WriteLine($"  Entrées         {entrees.Count}");

    if (entrees.Count > 0)
    {
        Titre("Ce que le registre contient");
        Console.WriteLine($"  {"Identité",-16} {"Pièce",-8} {"État",-11} {"Référence FNE",-24} Inscrit le");
        foreach (var entree in entrees)
        {
            Console.WriteLine(
                $"  {entree.Identite,-16} {entree.Piece,-8} {entree.Etat,-11} " +
                $"{(entree.ReferenceFne == "" ? "—" : entree.ReferenceFne),-24} " +
                $"{entree.CertifieeLe.ToLocalTime():dd/MM/yyyy HH:mm}");
        }
    }

    // Le registre a d'abord été posé à côté de l'exécutable, dans bin\. Si un
    // fichier y traîne encore, il porte peut-être des certifications que le
    // nouvel emplacement ignore. Rien n'est déplacé : c'est montré, et c'est tout.
    var ancien = ServicesMiddleware.AncienChemin(AppContext.BaseDirectory);
    if (!string.Equals(Path.GetFullPath(ancien), fichier.Chemin, StringComparison.OrdinalIgnoreCase)
        && File.Exists(ancien))
    {
        var fiche = new FileInfo(ancien);
        Titre("Un registre subsiste à l'ancien emplacement");
        Console.WriteLine($"  {Path.GetFullPath(ancien)}");
        Console.WriteLine($"  {fiche.Length} octets, modifié le {fiche.LastWriteTime:dd/MM/yyyy à HH:mm:ss}");
        Console.WriteLine();
        Console.WriteLine("  Ce dossier est une sortie de compilation : son contenu peut disparaître.");
        Console.WriteLine("  Rien n'a été déplacé. Pour lire ce registre-là :");
        Console.WriteLine($"    dotnet run --project src\\SageFne.Reader -- registre-info --registre \"{ancien}\"");
    }

    if (!fichier.Existe)
    {
        Console.WriteLine();
        Console.WriteLine("  Aucun fichier : rien n'a encore été inscrit ici. Si une facture a été");
        Console.WriteLine("  certifiée sans laisser de trace, « reconcilier » permet de l'inscrire.");
    }

    Console.WriteLine();
    Console.WriteLine("Ce registre est la SEULE mémoire des certifications : Sage n'en porte aucune.");
    Console.WriteLine("Sauvegardez-le. Le perdre ferait repartir à la DGI des factures déjà certifiées.");
    Console.WriteLine("Aucune API n'a été contactée, rien n'a été écrit.");

    return fichier.Existe ? 0 : 1;
}

// Les règles de classification des TVA à 0 %. Aucune API, aucune écriture Sage :
// ces commandes ne touchent que le registre des règles du middleware.
if (ligneDeCommande.Verbe == Verbe.ZeroVatRegle)
{
    var registreRegles = hote.Services.GetRequiredService<RegistreRegles>();
    var sujets = ligneDeCommande.Query.Pieces;
    var action = sujets.Count > 0 ? sujets[0].ToLowerInvariant() : "afficher";

    IReadOnlyList<RegleZeroVat> toutes;
    try
    {
        toutes = await registreRegles.ToutAsync();
    }
    catch (RegistreReglesIllisibleException erreur)
    {
        Console.Error.WriteLine(erreur.Message);
        return 1;
    }

    var courantes = toutes
        .GroupBy(regle => regle.Identite, StringComparer.OrdinalIgnoreCase)
        .Select(groupe => groupe.OrderByDescending(regle => regle.Version).First())
        .OrderBy(regle => regle.Portee).ThenBy(regle => regle.Cle)
        .ToList();

    if (action is "afficher")
    {
        Titre("Règles de TVA à 0 %");
        Console.WriteLine($"  Registre : {registreRegles.Chemin}");

        if (courantes.Count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Aucune règle. Toute ligne à 0 % reste bloquée, et c'est voulu :");
            Console.WriteLine("  le code FNE d'une exonération ne se devine pas.");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine(
            $"  {"Portée",-16} {"Clé",-14} {"Code",-8} {"État",-11} {"Fondement",-28} Preuve");
        foreach (var regle in courantes)
        {
            Console.WriteLine(
                $"  {regle.Portee,-16} {Tronquer(regle.Cle == "" ? "—" : regle.Cle, 14),-14} " +
                $"{regle.Code.Libelle(),-8} {regle.Etat,-11} " +
                $"{Tronquer(regle.Fondement.Libelle(), 28),-28} " +
                $"{(regle.Preuve == "" ? "— aucune —" : Tronquer(regle.Preuve, 30))}");
            Console.WriteLine($"      {regle.Reperage}" +
                $"{(regle.ValideePar == "" ? "" : $", validée par {regle.ValideePar}")}" +
                $"{(regle.ValideeLe is { } quand ? $" le {quand:dd/MM/yyyy}" : "")}" +
                $"{(regle.Motif == "" ? "" : $" — {regle.Motif}")}");
        }

        Console.WriteLine();
        Console.WriteLine($"  {toutes.Count} version(s) au total, {courantes.Count} règle(s) courante(s).");
        Console.WriteLine("  Le registre est en ajout seul : une modification crée une version.");
        return 0;
    }

    if (action is "verifier")
    {
        Titre("Vérification des règles de TVA à 0 %");
        var maintenant = DateTimeOffset.Now;
        var soucis = new List<string>();

        foreach (var regle in courantes)
        {
            if (regle.Empechement(maintenant) is { } pourquoi)
            {
                soucis.Add($"{regle.Portee} {regle.Cle} ({regle.Reperage}) : {pourquoi}.");
            }
            else if (regle.Reference == "")
            {
                soucis.Add(
                    $"{regle.Portee} {regle.Cle} ({regle.Reperage}) : validée sans référence. " +
                    "Rien ne dira sur quel document elle repose.");
            }
        }

        // Le piège que deux dictionnaires indexés par CT_Num tendent à coup sûr.
        var doubles = courantes
            .Where(regle => regle.Portee is PorteeRegle.RegimeAcheteur)
            .Select(regle => regle.Cle)
            .Intersect(
                courantes.Where(regle => regle.Portee is PorteeRegle.Client).Select(regle => regle.Cle),
                StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var compte in doubles)
        {
            soucis.Add(
                $"le compte {compte} porte à la fois un régime d'acheteur et une règle de client. " +
                "Le régime l'emporte ; la seconde ne servira jamais.");
        }

        var reglages = hote.Services.GetRequiredService<IOptions<FneOptions>>().Value.ZeroVat;
        var heritees =
            reglages.CustomerTaxRegimes.Count + reglages.ByArticle.Count +
            reglages.ByFamily.Count + reglages.ByCustomer.Count +
            (reglages.Default is "Unknown" or "" ? 0 : 1);

        if (heritees > 0)
        {
            soucis.Add(
                $"{heritees} déclaration(s) subsistent dans le paramétrage (Fne:ZeroVat). Elles ne " +
                "certifient rien : promouvez-les en règles validées, ou retirez-les.");
        }

        Console.WriteLine();
        if (soucis.Count == 0)
        {
            Console.WriteLine($"  {courantes.Count} règle(s), rien à signaler.");
            return 0;
        }

        foreach (var souci in soucis) Console.WriteLine($"  [à traiter] {souci}");
        Console.WriteLine();
        Console.WriteLine($"  {soucis.Count} point(s) à traiter.");
        return 1;
    }

    // --- Écriture ------------------------------------------------------------

    var portee = action switch
    {
        "article" => PorteeRegle.Article,
        "famille" => PorteeRegle.Famille,
        "client" => ligneDeCommande.Regime is null ? PorteeRegle.Client : PorteeRegle.RegimeAcheteur,
        "dossier" => PorteeRegle.Dossier,
        "revoquer" => PorteeRegle.Dossier,
        _ => (PorteeRegle?)null,
    };

    if (portee is null)
    {
        Console.Error.WriteLine(
            $"Action inconnue : « {action} ». Attendu : afficher, verifier, article, famille, " +
            "client, dossier, revoquer.");
        return 2;
    }

    var cleRegle = portee is PorteeRegle.Dossier ? "" : sujets.Count > 1 ? sujets[1] : "";
    if (portee is not PorteeRegle.Dossier && cleRegle == "" && action is not "revoquer")
    {
        Console.Error.WriteLine($"« zero-vat-regle {action} » attend une clé, par exemple : {action} 25SN001");
        return 2;
    }

    if (action is "revoquer")
    {
        var id = sujets.Count > 1 ? sujets[1] : "";
        var cible = courantes.FirstOrDefault(regle =>
            string.Equals(regle.Id, id, StringComparison.OrdinalIgnoreCase));

        if (cible is null)
        {
            Console.Error.WriteLine($"Aucune règle courante d'identifiant « {id} ».");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(ligneDeCommande.Motif))
        {
            Console.Error.WriteLine("Révoquer une règle demande un motif : --motif \"…\".");
            return 2;
        }

        Titre($"Révocation — {cible.Portee} {cible.Cle}");
        Console.WriteLine($"  Règle    {cible.Reperage}, code {cible.Code.Libelle()}, état {cible.Etat}");
        Console.WriteLine($"  Motif    {ligneDeCommande.Motif}");

        if (!ligneDeCommande.Confirme)
        {
            Console.WriteLine();
            Console.WriteLine("  Rien n'a été écrit : ajoutez --confirmer.");
            Console.WriteLine("  Les factures déjà certifiées sous cette règle ne changent pas.");
            return 1;
        }

        var revoquee = await registreRegles.AjouterAsync(cible with
        {
            Etat = EtatRegle.Revoquee,
            Note = ligneDeCommande.Motif!.Trim(),
        });

        Console.WriteLine();
        Console.WriteLine($"  Révoquée : {revoquee.Reperage}. Les versions précédentes restent au registre.");
        return 0;
    }

    var codeDemande = ConfiguredZeroVatPolicy.Analyser(ligneDeCommande.Code);
    if (ligneDeCommande.Code is not null && codeDemande is null)
    {
        Console.Error.WriteLine(
            $"--code vaut « {ligneDeCommande.Code} », qui n'est pas un code reconnu. " +
            "Attendu : Tvac, Tvad ou Unknown.");
        return 2;
    }

    if (portee is PorteeRegle.RegimeAcheteur
        && ConfiguredZeroVatPolicy.AnalyserRegimeAcheteur(ligneDeCommande.Regime) is null)
    {
        Console.Error.WriteLine($"--regime vaut « {ligneDeCommande.Regime} ». Attendu : TEE ou RME.");
        return 2;
    }

    if (!Enum.TryParse<FondementExoneration>(ligneDeCommande.Fondement, ignoreCase: true, out var fondementDemande))
    {
        fondementDemande = portee is PorteeRegle.RegimeAcheteur
            ? FondementExoneration.RegimeAcheteur
            : FondementExoneration.NonEtabli;
    }

    var valider = !ligneDeCommande.Brouillon;

    if (valider && string.IsNullOrWhiteSpace(ligneDeCommande.ValidePar))
    {
        Console.Error.WriteLine(
            "Une règle validée dit qui l'a validée : --valide-par \"…\". " +
            "Sans cela, écrivez-la en --brouillon : elle ne produira aucun code.");
        return 2;
    }

    // L'empreinte d'un justificatif conservé vaut preuve autant qu'une référence :
    // un arrêté scanné n'a pas toujours de numéro citable. La base pose la même
    // exigence dans les mêmes termes — l'une ou l'autre, jamais aucune.
    if (valider
        && string.IsNullOrWhiteSpace(ligneDeCommande.Reference)
        && string.IsNullOrWhiteSpace(ligneDeCommande.Empreinte))
    {
        Console.Error.WriteLine(
            "Une règle validée porte sa preuve : --reference \"…\" — réponse DGI, attestation, " +
            "numéro de convention — ou --empreinte \"…\" pour un justificatif conservé en " +
            "fichier. C'est ce qui répondra au contrôle dans six mois.");
        return 2;
    }

    var identiteRegle = $"{portee}/{cleRegle}".ToUpperInvariant();
    var precedente = courantes.FirstOrDefault(regle =>
        string.Equals(regle.Identite, identiteRegle, StringComparison.OrdinalIgnoreCase));

    var nouvelle = new RegleZeroVat
    {
        // Même forme que le regle_id de Supabase, pour que les deux registres se
        // recoupent sans table de correspondance.
        Id = precedente?.Id ?? portee.Value.ToString().ToLowerInvariant()
             + (cleRegle == "" ? "" : $"-{cleRegle.ToLowerInvariant()}"),
        Portee = portee.Value,
        Cle = cleRegle,
        Code = codeDemande ?? precedente?.Code ?? CodeTvaZero.Inconnu,
        Fondement = fondementDemande,
        // Le régime ne se porte que sur la portée qui le concerne : ailleurs, il
        // décrirait un acheteur que la règle ne vise pas.
        Regime = portee is PorteeRegle.RegimeAcheteur
            ? ligneDeCommande.Regime?.Trim().ToUpperInvariant() ?? ""
            : "",
        Etat = valider ? EtatRegle.Validee : EtatRegle.Brouillon,
        ValideePar = ligneDeCommande.ValidePar?.Trim() ?? "",
        ValideeLe = valider ? ligneDeCommande.ValideeLe ?? DateTimeOffset.Now : null,
        Reference = ligneDeCommande.Reference?.Trim() ?? "",
        EmpreinteJustificatif = ligneDeCommande.Empreinte?.Trim() ?? "",
        Motif = ligneDeCommande.Motif?.Trim() ?? "",
        ValideDu = ligneDeCommande.ValideDu,
        ValideAu = ligneDeCommande.ValideAu,
        Note = precedente is null ? "création" : $"remplace la version {precedente.Version}",
    };

    Titre($"Règle — {Nommer(portee.Value, cleRegle)}");
    Console.WriteLine($"  Code FNE     {nouvelle.Code.Libelle()}");
    Console.WriteLine($"  Fondement    {nouvelle.Fondement.Libelle()}");
    if (nouvelle.Regime != "") Console.WriteLine($"  Régime       {nouvelle.Regime}");
    Console.WriteLine($"  État         {nouvelle.Etat}");
    Console.WriteLine($"  Validée par  {(nouvelle.ValideePar == "" ? "—" : nouvelle.ValideePar)}");
    Console.WriteLine($"  Preuve       {(nouvelle.Preuve == "" ? "—" : nouvelle.Preuve)}");
    if (nouvelle.ValideDu is { } d) Console.WriteLine($"  À partir du  {d:dd/MM/yyyy}");
    if (nouvelle.ValideAu is { } f) Console.WriteLine($"  Jusqu'au     {f:dd/MM/yyyy}");
    if (precedente is not null)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"  Remplace {precedente.Reperage} (code {precedente.Code.Libelle()}, état {precedente.Etat}).");
        Console.WriteLine("  Cette version-là reste au registre : des factures en dépendent peut-être.");
    }

    if (!ligneDeCommande.Confirme)
    {
        Console.WriteLine();
        Console.WriteLine("  Rien n'a été écrit : ajoutez --confirmer.");
        return 1;
    }

    var inscrite = await registreRegles.AjouterAsync(nouvelle);
    Console.WriteLine();
    Console.WriteLine($"  Inscrite : {inscrite.Reperage}.");
    Console.WriteLine($"  Vérifiez : dotnet run --project src\\SageFne.Reader -- zero-vat-regle verifier");
    return 0;

    static string Nommer(PorteeRegle portee, string cle) => portee switch
    {
        PorteeRegle.RegimeAcheteur => $"régime acheteur du client {cle}",
        PorteeRegle.Article => $"article {cle}",
        PorteeRegle.Famille => $"famille {cle}",
        PorteeRegle.Client => $"client {cle}",
        _ => "dossier",
    };
}

// La campagne de saisie des NCC. Lecture seule : le NCC vit dans
// F_COMPTET.CT_Identifiant, il s'y corrige, et rien ici ne l'écrit. Cette
// commande dit seulement quels appels passer, et dans quel ordre.
if (ligneDeCommande.Verbe == Verbe.Ncc)
{
    var depotNcc = hote.Services.GetRequiredService<ISageInvoiceRepository>();

    Titre("Campagne NCC");
    Console.WriteLine("Source : base Sage (SQL Server), en lecture seule.");
    Console.WriteLine($"  Lecture de {ligneDeCommande.Query.Describe()}, limite {ligneDeCommande.Query.Limite}.");

    var entetesNcc = await depotNcc.GetInvoicesAsync(ligneDeCommande.Query);
    if (entetesNcc.Count == 0)
    {
        Console.WriteLine();
        Console.WriteLine("  Aucune facture lue : rien à examiner.");
        return 1;
    }

    var lignesNcc = await depotNcc.GetLinesAsync(
        ligneDeCommande.Query with { Pieces = entetesNcc.Select(entete => entete.Piece).ToList() });

    var clientsNcc = (await depotNcc.GetCustomersAsync(
            entetesNcc.Select(entete => entete.Tiers).Distinct().ToList()))
        .ToDictionary(client => client.CtNum, StringComparer.OrdinalIgnoreCase);

    var campagne = CampagneNcc.Analyser(entetesNcc, lignesNcc, clientsNcc);

    Titre("Où en est le dossier");
    Console.WriteLine($"  Factures lues            {campagne.Factures}");
    Console.WriteLine(
        $"  Prêtes de ce côté        {campagne.FacturesCouvertes} " +
        $"({Part(campagne.FacturesCouvertes, campagne.Factures)})");
    Console.WriteLine(
        $"  Incomplètes              {campagne.FacturesIncompletes} " +
        $"({Part(campagne.FacturesIncompletes, campagne.Factures)})");
    Console.WriteLine($"  Montant TTC en attente   {Somme(campagne.MontantIncomplet)}");
    Console.WriteLine($"  Comptes à renseigner     {campagne.Comptes.Count}");
    Console.WriteLine($"    dont sans NCC          {campagne.ComptesSansNcc}");
    Console.WriteLine($"    dont sans téléphone    {campagne.ComptesSansTelephone}");
    Console.WriteLine($"    dont sans les deux     {campagne.ComptesSansLesDeux}");
    Console.WriteLine($"  Comptes portant un NCC   {campagne.ComptesRenseignes}");

    if (campagne.Comptes.Count == 0)
    {
        Console.WriteLine();
        Console.WriteLine("  Aucun NCC ne manque sur ce périmètre.");
    }
    else
    {
        // Le montant classe la liste : c'est par lui qu'on décide à qui
        // téléphoner en premier. Le nombre de factures suit.
        Titre("Par quel appel commencer");
        Console.WriteLine(
            "  Classé par montant TTC en jeu. Tout se saisit sur la fiche client\n" +
            "  dans Sage : CT_Identifiant pour le NCC, CT_Telephone pour le téléphone.");
        var nifParlant = !campagne.TypeNifConstant;
        if (!nifParlant && campagne.Comptes.Count > 1)
        {
            Console.WriteLine(
                $"  CT_TypeNIF vaut {campagne.Comptes[0].TypeNif} sur les " +
                $"{campagne.Comptes.Count} comptes : ce champ ne distingue rien ici.");
        }

        Console.WriteLine();
        Console.WriteLine(
            $"  {"CT_Num",-16} {"Intitulé",-28} {"Fact.",5} {"Montant TTC",16} " +
            $"{"Cumul %",8}  {(nifParlant ? $"{"NIF",3}  " : "")}{"Dernière",10}  " +
            $"{"Manque",-10} Contact");

        var cumulNcc = 0m;
        var affiches = ligneDeCommande.Client is null ? 25 : campagne.Comptes.Count;
        var listeNcc = ligneDeCommande.Client is { } cibleNcc
            ? campagne.Comptes
                .Where(compte => compte.CtNum.Contains(cibleNcc, StringComparison.OrdinalIgnoreCase))
                .ToList()
            : campagne.Comptes;

        foreach (var compte in listeNcc.Take(affiches))
        {
            cumulNcc += compte.MontantTTC;
            Console.WriteLine(
                $"  {Tronquer(compte.CtNum, 16),-16} " +
                $"{Tronquer(compte.Intitule == "" ? "— fiche introuvable —" : compte.Intitule, 28),-28} " +
                $"{compte.Factures,5} {Somme(compte.MontantTTC),16} " +
                $"{(campagne.MontantIncomplet == 0 ? "—" : $"{cumulNcc / campagne.MontantIncomplet:P0}"),8}  " +
                $"{(nifParlant ? $"{compte.TypeNif,3}  " : "")}" +
                $"{compte.DerniereFacture,10:dd/MM/yyyy}  " +
                $"{compte.Manques,-10} {Tronquer(compte.MoyenDeContact, 20)}");
        }

        if (listeNcc.Count > affiches)
        {
            Console.WriteLine($"  … et {listeNcc.Count - affiches} autres comptes. --export les donne tous.");
        }

        Console.WriteLine();
        Console.WriteLine(
            $"  Par le montant : {campagne.ComptesPourMontant(0.5m)} compte(s) couvrent la moitié " +
            $"des {Somme(campagne.MontantIncomplet)} en attente,\n" +
            $"                   {campagne.ComptesPourMontant(0.8m)} en couvrent les quatre cinquièmes. " +
            "Ce sont les lignes ci-dessus.");

        // Deux classements, et souvent pas les mêmes comptes : quelques gros
        // clients portent l'essentiel du montant, quelques comptes à volume
        // portent l'essentiel des factures. Mais « souvent » n'est pas
        // « toujours » — sur un petit périmètre les deux coïncident, et
        // l'affirmer sans regarder serait dire une chose fausse avec aplomb.
        var parNombre = campagne.ParNombre;
        var tetePourNombre = campagne.ComptesPour(0.8m);
        var memesComptes = parNombre
            .Take(tetePourNombre)
            .Select(compte => compte.CtNum)
            .SequenceEqual(campagne.Comptes.Take(tetePourNombre).Select(compte => compte.CtNum),
                StringComparer.OrdinalIgnoreCase);

        Console.WriteLine();
        Console.WriteLine(
            $"  Par le nombre  : {campagne.ComptesPour(0.5m)} compte(s) couvrent la moitié des " +
            $"{Pluriel(campagne.FacturesIncompletes, "facture")},\n" +
            $"                   {tetePourNombre} en couvrent les quatre cinquièmes. " +
            (memesComptes
                ? "Ce sont les mêmes comptes."
                : "Ce ne sont pas les mêmes comptes."));

        if (!memesComptes)
        {
            Console.WriteLine();
            Console.WriteLine($"  {"CT_Num",-16} {"Intitulé",-28} {"Fact.",5} {"Cumul %",8}  Montant TTC");

            var cumulNombre = 0;
            foreach (var compte in parNombre.Take(8))
            {
                cumulNombre += compte.Factures;
                Console.WriteLine(
                    $"  {Tronquer(compte.CtNum, 16),-16} " +
                    $"{Tronquer(compte.Intitule == "" ? "— fiche introuvable —" : compte.Intitule, 28),-28} " +
                    $"{compte.Factures,5} {Part(cumulNombre, campagne.FacturesIncompletes),8}  " +
                    $"{Somme(compte.MontantTTC)}");
            }
        }

        var introuvables = campagne.Comptes.Count(compte => compte.FicheIntrouvable);
        var sansContact = campagne.Comptes.Count(compte => compte.MoyenDeContact == "— aucun —");

        if (introuvables > 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"  {introuvables} compte(s) facturés n'ont aucune fiche dans F_COMPTET. " +
                "Ce n'est\n  pas un NCC qui manque, c'est le client : à voir avant d'appeler.");
        }

        if (campagne.ComptesSansTelephone > 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"  {campagne.ComptesSansTelephone} compte(s) n'ont pas de téléphone dans Sage : " +
                "la DGI l'exige au même\n  titre que le NCC, et sans lui la pièce est bloquée.");

            // Ceux-là paraissaient prêts tant que le téléphone n'était qu'un
            // avertissement. Le dire à part, parce que ce sont les seuls dont
            // un unique champ sépare les factures de la certification.
            if (campagne.ComptesSansTelephoneSeulement > 0)
            {
                Console.WriteLine(
                    $"  {campagne.ComptesSansTelephoneSeulement} d'entre eux ont déjà leur NCC : " +
                    "seul le téléphone les retient, et leurs\n  factures paraissaient prêtes " +
                    "jusqu'ici.");
            }

            // Deux comptes différents, même s'ils tombent souvent au même
            // nombre : l'un dit ce qui bloque, l'autre ce qui manque pour aller
            // le chercher.
            if (sansContact > 0)
            {
                Console.WriteLine(
                    $"  {sansContact} n'ont ni téléphone ni courriel : rien pour les joindre, et " +
                    "c'est\n  justement ce qu'il faut aller chercher.");
            }
        }
    }

    // La liste courte : les clients sur lesquels une facture peut partir
    // aujourd'hui. Sans elle, un essai se saisit sur un client au hasard —
    // c'est-à-dire, sur ce dossier, sur un client bloqué neuf fois sur dix.
    if (campagne.Complets.Count > 0)
    {
        Titre("Clients prêts — fiche complète");
        Console.WriteLine(
            "  NCC et téléphone renseignés. Ce sont les seuls sur lesquels une\n" +
            "  facture peut être certifiée en l'état.");
        Console.WriteLine();
        Console.WriteLine($"  {"CT_Num",-18} {"Intitulé",-30} {"NCC",-18} Téléphone");
        foreach (var complet in campagne.Complets)
        {
            Console.WriteLine(
                $"  {Tronquer(complet.CtNum, 18),-18} {Tronquer(complet.Intitule, 30),-30} " +
                $"{Tronquer(complet.Ncc, 18),-18} {complet.Telephone}");
        }
    }

    // Ce que le dossier porte déjà. Aucune règle de format n'est affirmée : la
    // commande montre les formes observées, et c'est au lecteur de reconnaître
    // la sienne.
    if (campagne.Formes.Count > 0)
    {
        Titre("Les NCC déjà saisis, tels qu'ils sont");
        Console.WriteLine(
            "  Chiffres notés 9, lettres notées A. Ce n'est pas un format exigé :\n" +
            "  c'est ce que ce dossier porte, de quoi reconnaître une saisie douteuse\n" +
            "  au retour de campagne.");
        Console.WriteLine();
        Console.WriteLine($"  {"Forme",-24} {"Long.",5} {"Comptes",8}  Exemples");
        foreach (var forme in campagne.Formes.Take(10))
        {
            Console.WriteLine(
                $"  {Tronquer(forme.Gabarit, 24),-24} {forme.Longueur,5} {forme.Comptes,8}  " +
                $"{Tronquer(string.Join(", ", forme.Exemples), 40)}");
        }

        if (campagne.Formes.Count > 10)
        {
            Console.WriteLine($"  … et {campagne.Formes.Count - 10} autres formes.");
        }

        if (campagne.Formes.Count > 3)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"  {campagne.Formes.Count} formes différentes pour {campagne.ComptesRenseignes} comptes.\n" +
                "  Autant de formes que de saisies successives : à faire trancher avant\n" +
                "  d'en ajouter de nouvelles.");
        }
    }

    // Un même NCC sur deux comptes envoie les ventes de l'un sous le nom de
    // l'autre. C'est le défaut le plus coûteux de cette liste.
    if (campagne.Partages.Count > 0)
    {
        Titre("Un même NCC sur plusieurs comptes");
        Console.WriteLine(
            "  Les factures de ces comptes partiraient sous un seul contribuable.\n" +
            "  Presque toujours un copier-coller : à vérifier avant tout envoi.");
        Console.WriteLine();
        foreach (var partage in campagne.Partages.Take(15))
        {
            Console.WriteLine(
                $"  {Tronquer(partage.Ncc, 22),-22} {partage.Factures,5} facture(s)  " +
                $"{string.Join(", ", partage.Comptes)}");
        }

        if (campagne.Partages.Count > 15)
        {
            Console.WriteLine($"  … et {campagne.Partages.Count - 15} autres.");
        }
    }

    if (campagne.Ecarts.Count > 0)
    {
        Titre("Des NCC qui ne ressemblent pas aux autres du dossier");
        Console.WriteLine(
            "  Un écart n'est pas une faute : ce n'est pas à cette commande de dire\n" +
            "  quelle forme un NCC doit avoir. C'est une comparaison avec ce que ce\n" +
            "  dossier porte majoritairement — de quoi aller regarder la fiche.");
        Console.WriteLine();
        foreach (var ecart in campagne.Ecarts.Take(20))
        {
            Console.WriteLine(
                $"  {Tronquer(ecart.CtNum, 16),-16} {Tronquer(ecart.Intitule, 24),-24} " +
                $"{Tronquer(ecart.Ncc, 18),-18} {ecart.Observation}");
        }

        if (campagne.Ecarts.Count > 20)
        {
            Console.WriteLine($"  … et {campagne.Ecarts.Count - 20} autres.");
        }
    }

    if (campagne.Douteux.Count > 0)
    {
        Titre("Des valeurs présentes qui n'ont pas l'air d'un NCC");
        Console.WriteLine("  Signalées, pas corrigées : c'est dans Sage que cela se tranche.");
        Console.WriteLine();
        foreach (var douteux in campagne.Douteux.Take(20))
        {
            Console.WriteLine(
                $"  {Tronquer(douteux.CtNum, 16),-16} {Tronquer(douteux.Intitule, 26),-26} " +
                $"{Tronquer(douteux.Ncc, 18),-18} {douteux.Pourquoi}");
        }

        if (campagne.Douteux.Count > 20)
        {
            Console.WriteLine($"  … et {campagne.Douteux.Count - 20} autres.");
        }
    }

    // Le seul fichier écrit, et il est hors de Sage : un tableau à confier, qui
    // reviendra saisi à la main. Rien n'en revient tout seul.
    if (ligneDeCommande.Export is { } chemin)
    {
        var csv = new StringBuilder();
        // Deux colonnes vides, et dans cet ordre : la nature du tiers se tranche
        // avant d'aller chercher un NCC, parce qu'un particulier n'en a pas à
        // donner. Rien dans Sage ne porte cette distinction dans ce dossier —
        // c'est un jugement humain, ligne par ligne.
        csv.AppendLine("CT_Num;Intitule;Factures;MontantTTC;PremiereFacture;DerniereFacture;" +
                       "Ville;Telephone;Email;CT_TypeNIF;Manque;Nature_du_tiers;" +
                       "NCC_a_saisir;Telephone_a_saisir");

        static string Cellule(string valeur) =>
            valeur.Replace("\"", "\"\"").Replace(';', ',').Replace('\n', ' ').Replace('\r', ' ');

        foreach (var compte in campagne.Comptes)
        {
            csv.AppendLine(string.Join(';',
                Cellule(compte.CtNum),
                Cellule(compte.Intitule),
                compte.Factures.ToString(CultureInfo.InvariantCulture),
                // Virgule décimale : le fichier se sépare par « ; » comme
                // l'attend un Excel français, et « 50750.00 » y serait lu
                // comme du texte — une colonne de montants qui ne se trie pas.
                compte.MontantTTC.ToString("F2", CultureInfo.GetCultureInfo("fr-FR")),
                compte.PremiereFacture.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                compte.DerniereFacture.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                Cellule(compte.Ville),
                Cellule(compte.Telephone),
                Cellule(compte.Email),
                compte.TypeNif.ToString(CultureInfo.InvariantCulture),
                Cellule(compte.Manques),
                "",
                "",
                ""));
        }

        // Avec BOM : Excel lit l'UTF-8 sans, mais rend « SOCIÉTÉ » en « SOCIÃ‰TÃ‰ »,
        // et une liste d'appels illisible ne sert personne.
        await File.WriteAllTextAsync(chemin, csv.ToString(), new UTF8Encoding(true));

        Titre("Liste exportée");
        Console.WriteLine($"  {campagne.Comptes.Count} compte(s) écrits dans {Path.GetFullPath(chemin)}");
        Console.WriteLine(
            "  Trois colonnes vides : Nature_du_tiers d'abord — entreprise ou particulier —\n" +
            "  puis NCC_a_saisir, qui ne concerne que les entreprises, et\n" +
            "  Telephone_a_saisir, que la DGI exige pour tous. La colonne Manque dit\n" +
            "  lesquelles remplir pour chaque ligne.");
        Console.WriteLine("  Ce fichier ne revient pas tout seul dans Sage — la saisie s'y fait à la main.");
    }

    Console.WriteLine();
    Console.WriteLine("""
        Lecture seule. Rien n'a été écrit dans Sage, aucune facture n'a été envoyée.

        Tout se saisit dans Sage, sur la fiche client : CT_Identifiant pour le NCC,
        CT_Telephone pour le téléphone. Relancez cette commande ensuite — les
        compteurs diront ce que la campagne a gagné.
        """);
    return campagne.Comptes.Count == 0 ? 0 : 1;
}

// Inventaire des ventes à 0 % de TVA. Uniquement des SELECT, et surtout :
// aucune conclusion fiscale. La commande expose des faits, elle ne classe rien.
if (ligneDeCommande.Verbe == Verbe.AuditTvaZero)
{
    var depotAudit = hote.Services.GetRequiredService<ISageInvoiceRepository>();

    Titre("Audit des ventes à 0 % de TVA");
    Console.WriteLine("Source : base Sage (SQL Server), en lecture seule.");
    Console.WriteLine($"  Lecture de {ligneDeCommande.Query.Describe()}, limite {ligneDeCommande.Query.Limite}.");

    var entetesAudit = await depotAudit.GetInvoicesAsync(ligneDeCommande.Query);
    if (entetesAudit.Count == 0)
    {
        Console.WriteLine();
        Console.WriteLine("  Aucune facture lue : rien à examiner.");
        return 1;
    }

    var lignesAudit = await depotAudit.GetLinesAsync(
        ligneDeCommande.Query with { Pieces = entetesAudit.Select(entete => entete.Piece).ToList() });

    var clientsAudit = (await depotAudit.GetCustomersAsync(
            entetesAudit.Select(entete => entete.Tiers).Distinct().ToList()))
        .ToDictionary(client => client.CtNum, StringComparer.OrdinalIgnoreCase);

    var famillesAudit = await depotAudit.GetArticleFamiliesAsync(
        lignesAudit.Select(ligne => ligne.ArticleReference).Distinct().ToList());

    var audit = AuditTvaZero.Analyser(entetesAudit, lignesAudit, clientsAudit, famillesAudit);

    Console.WriteLine(
        $"  {entetesAudit.Count} facture(s), {audit.LignesExaminees} ligne(s) de vente examinées.");

    if (ligneDeCommande.AuditFiltre)
    {
        Console.WriteLine();
        Console.WriteLine("  Affichage restreint à " + string.Join(", ", new[]
        {
            ligneDeCommande.Article is { } a ? $"l'article {a}" : null,
            ligneDeCommande.Famille is { } f ? $"la famille {f}" : null,
            ligneDeCommande.Client is { } c ? $"le client {c}" : null,
        }.Where(partie => partie is not null)) + ".");
        Console.WriteLine("  L'analyse porte toujours sur tout le périmètre lu : seul l'affichage");
        Console.WriteLine("  est réduit, et les totaux ci-dessous restent ceux du dossier entier.");
    }

    if (audit.Articles.Count == 0)
    {
        Console.WriteLine();
        Console.WriteLine("  Aucune ligne à 0 % de TVA dans ce périmètre.");
        return 0;
    }

    Titre("Résumé");
    Console.WriteLine($"  Articles vendus à 0 %          {audit.Articles.Count}");
    Console.WriteLine($"    dont exclusivement à 0 %     {audit.ArticlesExclusivementAZero.Count}");
    Console.WriteLine($"    dont vendus aussi taxés      {audit.ArticlesAPlusieursTaux.Count}");
    Console.WriteLine($"  Familles concernées            {audit.Familles.Count}");
    Console.WriteLine($"  Clients concernés              {audit.Clients.Count}");
    Console.WriteLine($"  Factures concernées            {audit.NombreFacturesConcernees}");
    Console.WriteLine($"  Montant HT à 0 %               {audit.MontantHTTotal:N2}");

    if (ligneDeCommande.Article is { } referenceDemandee)
    {
        var detail = DetailArticle.Construire(
            referenceDemandee, entetesAudit, lignesAudit, clientsAudit, famillesAudit);

        if (detail is null)
        {
            Titre($"Article {referenceDemandee}");
            Console.WriteLine($"  Cet article n'apparaît sur aucune ligne du périmètre lu.");
            return 1;
        }

        Titre($"Article {detail.Reference} — toutes ses ventes");
        Console.WriteLine($"  {detail.Designation}");
        Console.WriteLine(
            $"  Famille {(detail.Famille == "" ? "— aucune —" : detail.Famille)}   " +
            $"{detail.Occurrences.Count} ligne(s), {detail.NombreFactures} facture(s), " +
            $"{detail.NombreClients} client(s)");

        Console.WriteLine();
        Console.WriteLine("  Répartition par taux de TVA effectif :");
        foreach (var (taux, nombre, montant) in detail.ParTaux)
        {
            Console.WriteLine($"    {taux,6:0.##} %   {nombre,5} ligne(s)   HT {montant,16:N2}");
        }

        Console.WriteLine();
        Console.WriteLine(detail switch
        {
            { ExclusivementAZero: true } =>
                "  Cet article n'est JAMAIS vendu taxé dans ce périmètre.",
            { Panache: true } =>
                "  Cet article est PANACHÉ : vendu tantôt à 0 %, tantôt taxé.",
            _ => "  Cet article n'est jamais vendu à 0 % dans ce périmètre.",
        });

        Titre($"Article {detail.Reference} — ligne par ligne");
        Console.WriteLine(
            $"  {"Pièce",-10} {"Date",-10} {"TVA",6}  {"Qté",10} {"HT",16}  " +
            $"{"Client",-24} {"NCC",-12} Codes de taxe");

        foreach (var occurrence in detail.Occurrences)
        {
            var codes = occurrence.Codes.Count == 0
                ? "— aucun —"
                : string.Join("  ", occurrence.Codes.Select(code =>
                    $"[{code.Position}] {(code.Code == "" ? "—" : code.Code)} {code.Taux:0.##}"));

            Console.WriteLine(
                $"  {occurrence.Piece,-10} {occurrence.Date:dd/MM/yyyy} {occurrence.TauxTva,6:0.##}  " +
                $"{occurrence.Quantite,10:N2} {occurrence.MontantHT,16:N2}  " +
                $"{Tronquer(occurrence.Client, 24),-24} " +
                $"{(occurrence.Ncc == "" ? "— absent —" : occurrence.Ncc),-12} {codes}");
        }
    }

    Titre("Par article");
    foreach (var article in audit.Articles.Where(article =>
                 (ligneDeCommande.Article is not { } reference
                  || string.Equals(article.Reference, reference, StringComparison.OrdinalIgnoreCase))
                 && (ligneDeCommande.Famille is not { } codeFamille
                     || string.Equals(article.Famille, codeFamille, StringComparison.OrdinalIgnoreCase))
                 && (ligneDeCommande.Client is not { } compte
                     || article.Clients.Any(c => string.Equals(c.Compte, compte, StringComparison.OrdinalIgnoreCase)))))
    {
        Console.WriteLine();
        Console.WriteLine(
            $"  {article.Reference}  {Tronquer(article.Designation, 40)}" +
            $"{(article.Famille == "" ? "" : $"   famille {article.Famille}")}");
        Console.WriteLine(
            $"    {article.LignesAZero} ligne(s) à 0 % sur {article.Factures} facture(s), " +
            $"{article.NombreClients} client(s) — quantité {article.QuantiteCumulee:N2}, " +
            $"HT {article.MontantHTCumule:N2}");

        Console.WriteLine(article.ExclusivementAZero
            ? "    Autres taux observés : AUCUN — cet article n'est jamais vendu taxé."
            : $"    Autres taux observés : {string.Join(", ", article.AutresTaux.Select(taux => $"{taux:0.##} %"))}" +
              " — le 0 % ne tient donc pas à l'article seul.");

        Console.WriteLine("    Codes de taxe sur les lignes à 0 % :");
        foreach (var code in article.CodesObserves)
        {
            Console.WriteLine(
                $"      DL_CodeTaxe{code.Position} = « {(code.Code == "" ? "—" : code.Code)} », " +
                $"DL_Taxe{code.Position} = {code.Taux:0.##}  ({code.Lignes} ligne(s))");
        }

        // Sans cette ligne, le total des codes semble démentir le nombre de
        // lignes : une ligne sans code n'apparaît nulle part au-dessus.
        if (article.LignesSansAucunCode > 0)
        {
            Console.WriteLine(
                $"      aucun code, les trois emplacements vides  " +
                $"({article.LignesSansAucunCode} ligne(s))");
        }

        Console.WriteLine("    Clients :");
        foreach (var client in article.Clients)
        {
            Console.WriteLine(
                $"      {client.Compte,-16} {Tronquer(client.Nom, 28),-28} " +
                $"NCC {(client.Ncc == "" ? "— absent —" : client.Ncc),-14} " +
                $"{client.Lignes} ligne(s), HT {client.MontantHT:N2}");
        }

        Console.WriteLine($"    Pièces : {string.Join(", ", article.ExemplesPieces)}");
    }

    Titre("Par famille d'article");
    Console.WriteLine($"  {"Famille",-14} {"à 0 %",7} {"taxées",8}  {"HT à 0 %",16}  Lecture");
    foreach (var famille in audit.Familles.Where(famille =>
                 ligneDeCommande.Famille is not { } code
                 || string.Equals(famille.Cle, code, StringComparison.OrdinalIgnoreCase)))
    {
        Console.WriteLine(
            $"  {Tronquer(famille.Libelle, 14),-14} {famille.LignesAZero,7} {famille.LignesTaxees,8}  " +
            $"{famille.MontantHTAZero,16:N2}  " +
            famille.Lecture);
    }

    Titre("Par client");
    Console.WriteLine($"  {"Client",-30} {"à 0 %",7} {"taxées",8}  {"HT à 0 %",16}  Lecture");
    foreach (var client in audit.Clients.Where(client =>
                 ligneDeCommande.Client is not { } compte
                 || string.Equals(client.Cle, compte, StringComparison.OrdinalIgnoreCase)))
    {
        Console.WriteLine(
            $"  {Tronquer(client.Libelle, 30),-30} {client.LignesAZero,7} {client.LignesTaxees,8}  " +
            $"{client.MontantHTAZero,16:N2}  " +
            client.Lecture);
    }

    Titre("Ce que ces chiffres ne disent pas");
    Console.WriteLine("""
          TVAC (exonération conventionnelle) et TVAD (exonération légale TEE/RME) valent
          tous deux 0 %. Rien de ce qui précède ne permet de les distinguer : Sage ne porte
          pas cette information, et aucun comptage ne la fera apparaître.

          Ce tableau sert à poser la bonne question à qui connaît le fondement juridique :
          un article jamais vendu taxé relève sans doute d'une règle d'article ; un client
          qui n'achète jamais taxé, d'une règle de client. Un article panaché signale que la
          règle est ailleurs — dans l'opération, ou dans une saisie à vérifier.

          La réponse se déclare ensuite dans Fne:ZeroVat, par article, famille, client ou
          dossier. Tant qu'elle manque, les pièces concernées restent bloquées, et c'est
          voulu.
          """);

    Console.WriteLine();
    Console.WriteLine("Lecture seule : uniquement des SELECT. Aucune API contactée, rien d'écrit.");
    Console.WriteLine("Aucune ligne n'a été classée TVAC ou TVAD.");

    return 0;
}

// Compléter le journal d'une pièce par un événement que le middleware n'a pas
// observé. Rien n'est déduit : l'exploitant dicte, et l'entrée porte sa marque.
if (ligneDeCommande.Verbe == Verbe.Journal)
{
    if (ligneDeCommande.Query.Pieces.Count != 1)
    {
        Console.Error.WriteLine(
            "journal attend un numéro de pièce, par exemple :\n" +
            "  journal 1072 --ajouter \"POST n° 1, HTTP 500\" --quand \"2026-08-31 23:40\" --confirmer");
        return 2;
    }

    var numeroJournal = ligneDeCommande.Query.Pieces[0];
    Titre($"Journal — pièce {numeroJournal}");
    Console.WriteLine("Aucune API n'est appelée. Sage reste en lecture seule.");
    Console.WriteLine("L'entrée sera marquée « reconstitué » : un fait saisi n'est pas un fait observé.");
    Console.WriteLine();

    var ajout = await hote.Services.GetRequiredService<InvoiceSender>()
        .AjouterAuJournalAsync(
            numeroJournal,
            ligneDeCommande.Evenement,
            ligneDeCommande.Quand,
            ligneDeCommande.CodeHttp,
            ligneDeCommande.Confirme);

    Console.WriteLine($"  {ajout.Message}");

    if (ajout.ConfirmationManque)
    {
        Console.WriteLine();
        Console.WriteLine("  Pour inscrire : ajoutez --confirmer.");
    }

    if (ajout.Applique)
    {
        Console.WriteLine();
        Console.WriteLine($"  Vérifiez : dotnet run --project src\\SageFne.Reader -- statut {numeroJournal}");
    }

    return ajout.Applique ? 0 : 1;
}

// Établir l'origine d'une certification que le registre ne qualifie pas. Les
// entrées antérieures au suivi de la source se relisent « inconnue », et une
// réconciliation manuelle qu'on ne reconnaît plus devient incorrigible.
if (ligneDeCommande.Verbe == Verbe.ReparerSource)
{
    if (ligneDeCommande.Query.Pieces.Count != 1)
    {
        Console.Error.WriteLine("reparer-source attend un numéro de pièce, par exemple : reparer-source 1052");
        return 2;
    }

    var numeroRepare = ligneDeCommande.Query.Pieces[0];
    Titre($"Origine de la certification — pièce {numeroRepare}");
    Console.WriteLine("Aucune API n'est appelée. Sage reste en lecture seule.");
    Console.WriteLine("Seule la source change : ni l'état, ni l'identité, ni l'empreinte,");
    Console.WriteLine("ni l'horodatage, ni la référence.");
    Console.WriteLine();

    var reparation = await hote.Services.GetRequiredService<InvoiceSender>()
        .ReparerSourceAsync(numeroRepare, ligneDeCommande.Confirme);

    Console.WriteLine($"  {reparation.Message}");

    if (reparation.ConfirmationManque)
    {
        Console.WriteLine();
        Console.WriteLine("  Pour appliquer : ajoutez --confirmer.");
    }

    if (reparation.Applique)
    {
        Console.WriteLine();
        Console.WriteLine($"  Vérifiez : dotnet run --project src\\SageFne.Reader -- statut {numeroRepare}");
    }

    return reparation.Applique ? 0 : 1;
}

// Corriger une réconciliation fautive. La certification reste acquise : seule
// la référence s'en va. Aucune API, et une copie du registre avant écriture.
if (ligneDeCommande.Verbe == Verbe.CorrigerReconciliation)
{
    if (ligneDeCommande.Query.Pieces.Count != 1)
    {
        Console.Error.WriteLine(
            "corriger-reconciliation attend un numéro de pièce, par exemple :\n" +
            "  corriger-reconciliation 1052 --supprimer-reference \\\n" +
            "    --reference-actuelle \"TA_REFERENCE_FNE\" --motif \"…\" --confirmer");
        return 2;
    }

    if (!ligneDeCommande.SupprimerReference)
    {
        Console.Error.WriteLine(
            "corriger-reconciliation attend --supprimer-reference : c'est la seule correction " +
            "qu'elle sache faire, et elle ne la devine pas.");
        return 2;
    }

    var numeroCorrige = ligneDeCommande.Query.Pieces[0];
    Titre($"Correction de réconciliation — pièce {numeroCorrige}");
    Console.WriteLine("Aucune API n'est appelée. Sage reste en lecture seule.");
    Console.WriteLine("La certification n'est pas défaite : seule la référence est retirée.");
    Console.WriteLine();

    var correction = await hote.Services.GetRequiredService<InvoiceSender>()
        .CorrigerReferenceAsync(
            numeroCorrige,
            ligneDeCommande.ReferenceActuelle,
            ligneDeCommande.Motif,
            ligneDeCommande.SupprimerJeton,
            ligneDeCommande.Confirme);

    Console.WriteLine($"  {correction.Message}");

    if (correction.ConfirmationManque)
    {
        Console.WriteLine();
        Console.WriteLine("  Pour appliquer : ajoutez --confirmer.");
    }

    if (correction.Applique)
    {
        Console.WriteLine();
        Console.WriteLine($"  Vérifiez : dotnet run --project src\\SageFne.Reader -- statut {numeroCorrige}");
    }

    return correction.Applique ? 0 : 1;
}

// Inscrire au registre une certification constatée sur le portail. Aucune API,
// et rien n'est écrit sans --confirmer.
if (ligneDeCommande.Verbe == Verbe.Reconcilier)
{
    if (ligneDeCommande.Query.Pieces.Count != 1)
    {
        Console.Error.WriteLine(
            "reconcilier attend un numéro de pièce, par exemple :\n" +
            "  reconcilier 1052 --reference \"2304903U26000000930\" --confirmer");
        return 2;
    }

    var numeroReconcilie = ligneDeCommande.Query.Pieces[0];
    Titre($"Réconciliation — pièce {numeroReconcilie}");
    Console.WriteLine("Aucune API n'est appelée. Sage reste en lecture seule.");
    Console.WriteLine();

    var reconciliation = await hote.Services.GetRequiredService<InvoiceSender>()
        .ReconcilierAsync(
            numeroReconcilie,
            ligneDeCommande.Reference,
            ligneDeCommande.Jeton,
            ligneDeCommande.Confirme,
            ligneDeCommande.SansReference,
            ligneDeCommande.Motif);

    Console.WriteLine($"  {reconciliation.Message}");

    if (reconciliation.ConfirmationManque)
    {
        Console.WriteLine();
        Console.WriteLine("  Vérifiez la référence ci-dessus contre le PDF ou le portail avant de");
        Console.WriteLine("  l'inscrire : une fois posée, elle ne se réécrit pas.");
        Console.WriteLine("  Pour inscrire : ajoutez --confirmer.");
    }

    if (reconciliation.Applique)
    {
        Console.WriteLine();
        Console.WriteLine($"  Vérifiez : dotnet run --project src\\SageFne.Reader -- statut {numeroReconcilie}");
    }

    return reconciliation.Applique ? 0 : 1;
}

// Ce que le registre local sait d'une pièce. Ni appel, ni écriture : deux
// SELECT sur Sage et une lecture du registre. La clé d'API n'est jamais lue,
// donc jamais affichable.
if (ligneDeCommande.Verbe == Verbe.Statut)
{
    if (ligneDeCommande.Query.Pieces.Count != 1)
    {
        Console.Error.WriteLine("statut attend un numéro de pièce, par exemple : statut 1052");
        return 2;
    }

    var numeroStatut = ligneDeCommande.Query.Pieces[0];
    var lotStatut = await hote.Services.GetRequiredService<InvoiceBatchReader>()
        .ReadAsync(InvoiceQuery.Piece(numeroStatut));
    var suivie = lotStatut.Conversions.FirstOrDefault();

    Titre($"Statut — pièce {numeroStatut}");
    Console.WriteLine("Registre local du middleware. Sage n'est lu qu'en SELECT.");

    if (suivie is null)
    {
        Console.WriteLine();
        Console.WriteLine($"  Aucune facture au numéro {numeroStatut} dans Sage.");
        return 1;
    }

    Titre("Côté Sage");
    Console.WriteLine($"  Identité      {suivie.Header.Identite}");
    Console.WriteLine($"  DO_Type       {suivie.Header.Type} — DO_DocType {suivie.Header.DocType}");
    Console.WriteLine($"  Date          {suivie.Header.Date:dd/MM/yyyy}");
    Console.WriteLine($"  Client        {suivie.Customer?.Intitule ?? suivie.Header.Tiers}");
    Console.WriteLine($"  Total TTC     {suivie.TotalTTC:N2}");
    Console.WriteLine($"  Empreinte     {(suivie.Empreinte == "" ? "— non calculable, la pièce ne se traduit pas —" : suivie.Empreinte)}");

    Titre("Côté registre");
    var connue = suivie.Certification;

    if (connue is null)
    {
        Console.WriteLine("  Aucune trace : cette pièce n'a jamais été envoyée.");
    }
    else
    {
        // Une certification sans référence n'est pas une certification douteuse :
        // la plateforme d'essai n'en publie pas toujours. Le dire « non
        // disponible » plutôt que « aucune » évite de faire douter de l'état.
        var manquante = connue.Etat == EtatFne.Certified ? "— non disponible —" : "— aucune —";

        Console.WriteLine($"  État          {connue.Etat}");
        Console.WriteLine($"  Référence FNE {(connue.SansReference ? manquante : connue.ReferenceFne)}");
        Console.WriteLine($"  Jeton (QR)    {(connue.Token == "" ? manquante : connue.Token)}");
        Console.WriteLine($"  Source        {InvoiceSender.Nommer(connue.Source)}");
        Console.WriteLine($"  Horodatage    {connue.CertifieeLe.ToLocalTime():dd/MM/yyyy à HH:mm:ss}");
        Console.WriteLine($"  Identité      {connue.Identite}");
        Console.WriteLine($"  Empreinte     {(connue.Empreinte == "" ? "— aucune —" : connue.Empreinte)}");

        if (connue.Erreur != "") Console.WriteLine($"  Réponse       {connue.Erreur}");

        if (connue.Source == SourceCertification.Inconnue)
        {
            Console.WriteLine();
            Console.WriteLine("  Cette entrée est antérieure au suivi de la source. Tant que son");
            Console.WriteLine("  origine n'est pas établie, elle ne peut pas être corrigée :");
            Console.WriteLine($"    dotnet run --project src\\SageFne.Reader -- reparer-source {numeroStatut}");
        }

        foreach (var ligne in connue.Motif.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Console.WriteLine($"  Motif         {ligne}");
        }

        if (connue.Tentatives.Count > 0)
        {
            Titre("Journal de la pièce");
            foreach (var tentative in connue.Chronologie)
            {
                Console.WriteLine($"  {tentative.Decrire()}");
            }

            // Le compte des envois est ce qui manquait le soir du doublon : rien
            // ne rappelait qu'un POST était déjà parti.
            if (connue.NombreEnvois > 1)
            {
                Console.WriteLine();
                Console.WriteLine(
                    $"  ATTENTION : {connue.NombreEnvois} envois sont partis vers la DGI pour cette");
                Console.WriteLine(
                    "  pièce. Comptez les factures au portail, pas seulement leur présence.");
            }
        }

        // L'égalité des empreintes est ce qui sépare « certifiée et inchangée »
        // de « certifiée puis modifiée dans Sage ».
        if (connue.Empreinte != "" && suivie.Empreinte != "")
        {
            Console.WriteLine();
            Console.WriteLine(connue.Empreinte == suivie.Empreinte
                ? "  Empreinte     concordante — la pièce n'a pas bougé depuis cet envoi."
                : "  Empreinte     DIVERGENTE — la pièce a changé dans Sage depuis cet envoi.");
        }
    }

    Titre("Ce que cela autorise");
    Console.WriteLine($"  État retenu   {suivie.LibelleEtat}");
    Console.WriteLine(suivie.Etat switch
    {
        EtatPiece.ACertifier =>
            $"  La pièce peut partir : dotnet run --project src\\SageFne.Reader -- envoyer {numeroStatut}",
        EtatPiece.DejaCertifiee =>
            "  Elle ne repartira pas : elle est certifiée et n'a pas changé.",
        EtatPiece.ModifieeDepuis =>
            "  Elle ne repartira pas : certifiée puis modifiée. La correction passe par un avoir.",
        EtatPiece.EnSuspens =>
            "  Elle ne repartira pas seule. Cherchez-la sur le portail DGI, puis :\n" +
            $"    debloquer {numeroStatut} --transmise --confirmer      (elle y est, pas encore certifiée)\n" +
            $"    debloquer {numeroStatut} --reference REF --confirmer  (elle y est, certifiée sous ce numéro)\n" +
            $"    debloquer {numeroStatut} --sans-reference --confirmer (elle y est, certifiée sans numéro)\n" +
            $"    debloquer {numeroStatut} --non-certifiee --confirmer  (le portail ne la connaît pas)",
        EtatPiece.Transmise =>
            "  Elle est déjà au portail : la renvoyer l'y mettrait deux fois.\n" +
            "  Une fois le clic passé et la référence en main :\n" +
            $"    debloquer {numeroStatut} --reference REF --confirmer\n" +
            $"    debloquer {numeroStatut} --sans-reference --confirmer (si le clic ne publie aucun numéro)",
        _ => "  Elle ne peut pas partir : des contrôles la bloquent.",
    });

    if (suivie.Report.Constats.Count > 0)
    {
        Titre("Contrôles");
        Constats(suivie.Report.Constats);
    }

    Console.WriteLine();
    Console.WriteLine("Aucune API n'a été contactée, rien n'a été écrit — ni dans Sage, ni au registre.");

    return suivie.Etat is EtatPiece.Bloquee or EtatPiece.EnSuspens or EtatPiece.Transmise ? 1 : 0;
}

// Trancher le sort d'une pièce restée « en suspens ». Aucune API n'est
// appelée : la commande inscrit au registre ce que l'exploitant a lu sur le
// portail de la DGI, parce que personne d'autre ne peut le savoir.
if (ligneDeCommande.Verbe == Verbe.Debloquer)
{
    if (ligneDeCommande.Query.Pieces.Count != 1)
    {
        Console.Error.WriteLine(
            "debloquer attend un numéro de pièce, par exemple : debloquer 1052 --non-certifiee --confirmer");
        return 2;
    }

    var numeroDeblocage = ligneDeCommande.Query.Pieces[0];
    Titre($"Déblocage — pièce {numeroDeblocage}");
    Console.WriteLine("Aucune API n'est appelée. Sage reste en lecture seule.");
    Console.WriteLine();

    var resolveur = hote.Services.GetRequiredService<InvoiceSender>();
    var deblocage = await resolveur.DebloquerAsync(
        numeroDeblocage,
        ligneDeCommande.Reference,
        ligneDeCommande.NonCertifiee,
        ligneDeCommande.Confirme,
        ligneDeCommande.SansReference,
        ligneDeCommande.Motif,
        ligneDeCommande.Transmise);

    Console.WriteLine($"  {deblocage.Message}");

    if (deblocage.ConfirmationManque)
    {
        Console.WriteLine();
        Console.WriteLine("  Pour inscrire cette décision au registre, ajoutez --confirmer.");
    }

    return deblocage.Applique ? 0 : 1;
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
    var clientFne = hote.Services.GetRequiredService<IFneApiClient>();

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

    if (aEnvoyer.Certification is { NombreEnvois: > 0 } dejaParti)
    {
        Console.WriteLine(
            $"  ATTENTION : {dejaParti.NombreEnvois} envoi(s) sont déjà partis pour cette pièce.");
        Console.WriteLine(
            "  Un 5xx ne veut pas dire que la DGI n'a rien enregistré : elle a déjà certifié");
        Console.WriteLine(
            "  des factures en répondant 500, sans les publier tout de suite au portail.");
        Console.WriteLine();
    }

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
    var politique = hote.Services.GetRequiredService<IZeroVatPolicy>();
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
        var taxes = TaxMapping.Read(ligne, decision.Code, catalogueDuReleve);
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
        Console.WriteLine(
            $"  {"Ligne",5} {"Article",-14} {"Famille",-10} {"Règle appliquée",-28} " +
            $"{"Code FNE",-9} Fondement");
        foreach (var (ligne, decision) in zero)
        {
            Console.WriteLine(
                $"  {ligne.Ligne,5} {Tronquer(ligne.ArticleReference, 14),-14} " +
                $"{Tronquer(famillesDuReleve.GetValueOrDefault(ligne.ArticleReference, "—"), 10),-10} " +
                $"{Tronquer(decision.Origine, 28),-28} {decision.Code.Libelle(),-9} " +
                $"{decision.Fondement.Libelle()}");
            if (decision.Erreur is not null) Console.WriteLine($"        ERREUR : {decision.Erreur}");
            if (decision.Avertissement is not null) Console.WriteLine($"        à noter : {decision.Avertissement}");
        }

        Console.WriteLine();
        Console.WriteLine("""
              Ordre consulté : régime de l'acheteur, puis article, famille, client, dossier.
              Les règles vivent au registre — « zero-vat-regle afficher » les montre.

              Le code FNE est ce qui part dans items[].taxes. Le fondement dit pourquoi, et
              ne s'en déduit pas. Une règle ne produit son code qu'une fois validée sur une
              preuve : en brouillon, elle bloque, et c'est ce qu'on attend d'elle.
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
        Champ("isRne", facture.IsRne ? "true" : "false",
            "paramétrage Fne:IsRne — votre régime, pas celui du client");
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
