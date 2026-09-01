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

    /// <summary>
    /// Envoyer une facture à la certification. Simule par défaut : seul
    /// <c>--confirmer</c> déclenche l'appel réel.
    /// </summary>
    Envoyer,

    /// <summary>
    /// Vérifier la configuration d'accès à la plateforme. N'appelle aucune API.
    /// </summary>
    Verification,

    /// <summary>
    /// Trancher le sort d'une pièce dont l'envoi est resté en suspens.
    /// </summary>
    /// <remarks>
    /// Rien ne peut le faire à notre place : seul le portail de la DGI dit si
    /// la facture y est arrivée. La commande n'appelle aucune API — elle
    /// inscrit au registre ce que l'exploitant y a lu.
    /// </remarks>
    Debloquer,

    /// <summary>
    /// Ce que le registre local sait d'une pièce : état, référence, date.
    /// </summary>
    /// <remarks>
    /// Ni appel, ni écriture. C'est la commande à lancer après un envoi pour
    /// vérifier ce qui a été retenu, et avant un envoi pour savoir si la pièce
    /// peut partir.
    /// </remarks>
    Statut,

    /// <summary>Où vit le registre, ce qu'il pèse, ce qu'il contient.</summary>
    RegistreInfo,

    /// <summary>
    /// Inscrire au registre une certification constatée sur le portail DGI.
    /// </summary>
    /// <remarks>
    /// La seule façon de rattraper une certification dont la trace a été
    /// perdue. N'appelle aucune API : la référence vient de l'exploitant.
    /// </remarks>
    Reconcilier,

    /// <summary>
    /// Corriger une réconciliation fautive sans défaire la certification.
    /// </summary>
    CorrigerReconciliation,

    /// <summary>
    /// Établir l'origine d'une certification que le registre ne qualifie pas.
    /// </summary>
    ReparerSource,

    /// <summary>
    /// Compléter le journal d'une pièce par un événement non observé.
    /// </summary>
    Journal,

    /// <summary>
    /// Inventorier les ventes à 0 % de TVA, sans rien en conclure.
    /// </summary>
    AuditTvaZero,
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

    /// <summary>
    /// L'envoi réel a été demandé explicitement.
    /// </summary>
    /// <remarks>
    /// Une certification ne s'annule pas : elle se corrige par un avoir. Le
    /// défaut est donc la simulation, et il faut écrire <c>--confirmer</c> pour
    /// que quoi que ce soit parte.
    /// </remarks>
    public bool Confirme { get; init; }
    public InvoiceQuery Query { get; init; } = new();
    /// <summary>Dossier où écrire un fichier JSON par pièce.</summary>
    public string? Sortie { get; init; }
    /// <summary>Afficher le JSON de toutes les pièces, et pas seulement le résumé.</summary>
    public bool AfficherJson { get; init; }
    /// <summary>Registre des certifications à consulter, à la place de celui configuré.</summary>
    public string? Registre { get; init; }

    /// <summary>Référence lue sur le portail DGI, pour <c>debloquer</c>.</summary>
    public string? Reference { get; init; }

    /// <summary>Le portail ne connaît pas la pièce : elle peut repartir.</summary>
    public bool NonCertifiee { get; init; }

    /// <summary>Jeton de vérification (QR), pour <c>reconcilier</c>.</summary>
    public string? Jeton { get; init; }

    /// <summary>
    /// Le portail ne publie aucune référence : constat explicite.
    /// </summary>
    /// <remarks>
    /// Exigé plutôt que déduit de l'absence de <c>--reference</c> : une faute
    /// de frappe passerait sinon pour un constat.
    /// </remarks>
    public bool SansReference { get; init; }

    /// <summary>Retirer la référence d'une certification, pour <c>corriger-reconciliation</c>.</summary>
    public bool SupprimerReference { get; init; }

    /// <summary>Retirer aussi le jeton.</summary>
    public bool SupprimerJeton { get; init; }

    /// <summary>Référence que l'appelant s'attend à trouver au registre.</summary>
    public string? ReferenceActuelle { get; init; }

    /// <summary>Pourquoi cette décision a été prise. Conservé au registre.</summary>
    public string? Motif { get; init; }

    /// <summary>Événement à inscrire au journal, pour <c>journal</c>.</summary>
    public string? Evenement { get; init; }

    /// <summary>Quand cet événement a eu lieu — sa date réelle, non celle de la saisie.</summary>
    public DateTimeOffset? Quand { get; init; }

    /// <summary>Code HTTP de l'événement reconstitué, s'il est connu.</summary>
    public int? CodeHttp { get; init; }
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
        var confirme = false;
        string? reference = null;
        string? jeton = null;
        string? motif = null;
        string? referenceActuelle = null;
        string? evenement = null;
        DateTimeOffset? quand = null;
        int? codeHttp = null;
        var nonCertifiee = false;
        var sansReference = false;
        var supprimerReference = false;
        var supprimerJeton = false;

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
                case "apercu":
                case "aperçu":
                    verbe = Verbe.Detail;
                    break;
                case "colonnes":
                    verbe = Verbe.Colonnes;
                    break;
                case "taxes":
                    verbe = Verbe.Taxes;
                    break;
                case "fne-check":
                    verbe = Verbe.Verification;
                    break;
                case "envoyer":
                    verbe = Verbe.Envoyer;
                    break;
                case "debloquer":
                case "débloquer":
                    verbe = Verbe.Debloquer;
                    break;
                case "statut":
                case "status":
                    verbe = Verbe.Statut;
                    break;
                case "registre-info":
                    verbe = Verbe.RegistreInfo;
                    break;
                case "reconcilier":
                case "réconcilier":
                    verbe = Verbe.Reconcilier;
                    break;
                case "corriger-reconciliation":
                case "corriger-réconciliation":
                    verbe = Verbe.CorrigerReconciliation;
                    break;
                case "reparer-source":
                case "réparer-source":
                    verbe = Verbe.ReparerSource;
                    break;
                case "journal":
                    verbe = Verbe.Journal;
                    break;
                case "audit-tva-zero":
                case "audit-tva-0":
                    verbe = Verbe.AuditTvaZero;
                    // Le 0 % se cherche sur tout le dossier : la limite du dry
                    // run passerait à côté de la moitié des cas.
                    if (limite == LimiteParDefaut) limite = 2000;
                    break;
                case "--ajouter":
                    evenement = Valeur() ?? "";
                    if (evenement is "") erreurs.Add("--ajouter attend la description de l'événement.");
                    break;
                case "--quand":
                    var lue = Valeur();
                    if (DateTimeOffset.TryParse(lue, out var date)) quand = date;
                    else erreurs.Add("--quand attend une date et une heure, par exemple 2026-08-31 23:40.");
                    break;
                case "--code-http":
                    if (int.TryParse(Valeur(), out var http) && http is >= 100 and <= 599) codeHttp = http;
                    else erreurs.Add("--code-http attend un code entre 100 et 599.");
                    break;
                case "--sans-reference":
                case "--sans-référence":
                    sansReference = true;
                    break;
                case "--supprimer-reference":
                case "--supprimer-référence":
                    supprimerReference = true;
                    break;
                case "--supprimer-jeton":
                case "--supprimer-token":
                    supprimerJeton = true;
                    break;
                case "--reference-actuelle":
                case "--référence-actuelle":
                    referenceActuelle = Valeur() ?? "";
                    if (referenceActuelle is "")
                        erreurs.Add("--reference-actuelle attend la référence que porte le registre aujourd'hui.");
                    break;
                case "--motif":
                    motif = Valeur() ?? "";
                    if (motif is "") erreurs.Add("--motif attend une phrase expliquant la décision.");
                    break;
                case "--token":
                case "--jeton":
                    jeton = Valeur() ?? "";
                    if (jeton is "") erreurs.Add("--token attend le jeton lu sur le portail DGI.");
                    break;
                case "--reference":
                case "--référence":
                    reference = Valeur() ?? "";
                    if (reference is "") erreurs.Add("--reference attend la référence lue sur le portail DGI.");
                    break;
                case "--non-certifiee":
                case "--non-certifiée":
                    nonCertifiee = true;
                    break;
                case "--confirmer":
                    confirme = true;
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
            Confirme = confirme,
            Query = new InvoiceQuery
            {
                Pieces = pieces,
                Depuis = depuis,
                Jusqua = jusqua,
                Limite = limite,
            },
            Sortie = sortie,
            Registre = registre,
            Reference = reference,
            Jeton = jeton,
            Motif = motif,
            ReferenceActuelle = referenceActuelle,
            Evenement = evenement,
            Quand = quand,
            CodeHttp = codeHttp,
            NonCertifiee = nonCertifiee,
            SansReference = sansReference,
            SupprimerReference = supprimerReference,
            SupprimerJeton = supprimerJeton,
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
          dotnet run --project src/SageFne.Reader -- apercu 1052         aperçu FNE d'une pièce, sans rien envoyer
          dotnet run --project src/SageFne.Reader -- detail 1219         idem (même commande)
          dotnet run --project src/SageFne.Reader -- colonnes            colonnes réelles des tables Sage
          dotnet run --project src/SageFne.Reader -- taxes 1219          paramétrage fiscal autour d'une pièce
          dotnet run --project src/SageFne.Reader -- candidats-fne       factures d'essai fiscalement nettes
          dotnet run --project src/SageFne.Reader -- audit-tva-zero      inventaire des ventes à 0 % de TVA
          dotnet run --project src/SageFne.Reader -- fne-check           vérifie l'accès FNE, sans rien appeler
          dotnet run --project src/SageFne.Reader -- envoyer 1052        montre la requête, n'envoie rien
          dotnet run --project src/SageFne.Reader -- envoyer 1052 --confirmer   envoie pour de vrai
          dotnet run --project src/SageFne.Reader -- statut 1052        ce que le registre sait d'une pièce
          dotnet run --project src/SageFne.Reader -- registre-info      où vit le registre, ce qu'il contient
          dotnet run --project src/SageFne.Reader -- reconcilier 1052 --reference REF --confirmer
          dotnet run --project src/SageFne.Reader -- reparer-source 1052   origine d'une entrée ancienne
          dotnet run --project src/SageFne.Reader -- journal 1072 --ajouter "..." --quand "..." --confirmer
          dotnet run --project src/SageFne.Reader -- corriger-reconciliation 1052 --supprimer-reference ...
          dotnet run --project src/SageFne.Reader -- debloquer 1052 --non-certifiee --confirmer
          dotnet run --project src/SageFne.Reader -- debloquer 1052 --reference REF --confirmer

        Options :
          --du, --au     période, bornes comprises
          --limite N     nombre maximal de pièces (500 par défaut)
          --sortie DOS   écrit un fichier JSON par pièce dans ce dossier
          --registre F   registre des certifications à consulter
          --json         affiche le JSON de chaque pièce, pas seulement le résumé
          --confirmer    autorise l'envoi réel à la DGI ; sans lui, tout est simulé

        Débloquer une pièce restée « en suspens » — après l'avoir cherchée sur le portail DGI :
          --reference REF   elle y figure, sous ce numéro
          --sans-reference  elle y figure, sans numéro publié
          --non-certifiee   elle n'y figure pas — exige --motif et un délai depuis l'envoi

        Réconcilier une certification dont la trace manque au registre :
          --reference REF     référence FNE relevée sur le portail ou le PDF
          --sans-reference    le portail n'en publie aucune — exige --motif
          --token JETON       jeton du QR code, s'il figure sur le PDF
          --motif "…"         pourquoi, conservé au registre

        Compléter le journal d'une pièce par un événement que le middleware n'a pas observé :
          --ajouter "…"     ce qui s'est passé
          --quand "…"       quand — la date des faits, pas celle de la saisie
          --code-http N     le code reçu, s'il est connu
        L'entrée est marquée « reconstitué » : elle ne se confond jamais avec un fait observé.

        Corriger une réconciliation fautive, sans défaire la certification :
          --supprimer-reference       retire la référence, garde l'état Certified
          --supprimer-jeton           retire aussi le jeton
          --reference-actuelle "…"    ce que le registre doit porter, sinon refus
          --motif "…"                 obligatoire
        """;
}
