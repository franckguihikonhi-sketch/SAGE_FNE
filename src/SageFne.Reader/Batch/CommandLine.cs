using SageFne.Core.Data;

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

    /// <summary>
    /// Consulter et écrire les règles de classification des TVA à 0 %.
    /// </summary>
    ZeroVatRegle,

    /// <summary>
    /// L'état de la campagne de saisie des NCC : ce qui manque, et par quel
    /// compte commencer. Lecture seule, et le NCC se saisit dans Sage.
    /// </summary>
    Ncc,
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

    /// <summary>
    /// La pièce figure au portail mais n'y est pas encore certifiée : le clic
    /// reste à faire.
    /// </summary>
    public bool Transmise { get; init; }

    /// <summary>Retirer la référence d'une certification, pour <c>corriger-reconciliation</c>.</summary>
    public bool SupprimerReference { get; init; }

    /// <summary>Retirer aussi le jeton.</summary>
    public bool SupprimerJeton { get; init; }

    /// <summary>
    /// L'appelant déclare que le registre ne porte aujourd'hui aucune référence.
    /// </summary>
    /// <remarks>
    /// À ne pas confondre avec <see cref="SansReference"/>, qui déclare une
    /// certification acquise sans numéro. Celle-ci décrit ce qu'on s'attend à
    /// trouver ; l'autre, ce qu'on veut inscrire.
    /// </remarks>
    public bool SansReferenceActuelle { get; init; }

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

    /// <summary>
    /// Restreindre l'affichage de l'audit à une référence d'article.
    /// </summary>
    /// <remarks>
    /// Ces trois filtres ne touchent pas l'analyse : elle porte toujours sur
    /// tout le périmètre lu. Ils ne font que réduire ce qui s'imprime — la
    /// sortie complète étant trop volumineuse pour être lue d'un bloc.
    /// </remarks>
    public string? Article { get; init; }

    /// <summary>Restreindre l'affichage à une famille d'article.</summary>
    public string? Famille { get; init; }

    /// <summary>Restreindre l'affichage à un compte client.</summary>
    public string? Client { get; init; }

    /// <summary>Vrai quand l'affichage de l'audit est restreint.</summary>
    public bool AuditFiltre => Article is not null || Famille is not null || Client is not null;

    /// <summary>Code FNE d'une règle : <c>Tvac</c>, <c>Tvad</c> ou <c>Unknown</c>.</summary>
    public string? Code { get; init; }

    /// <summary>Régime déclaré de l'acheteur : <c>TEE</c> ou <c>RME</c>.</summary>
    public string? Regime { get; init; }

    /// <summary>Fondement juridique de la règle.</summary>
    public string? Fondement { get; init; }

    /// <summary>Qui a validé la règle.</summary>
    public string? ValidePar { get; init; }

    /// <summary>Empreinte du justificatif conservé.</summary>
    public string? Empreinte { get; init; }

    public DateTimeOffset? ValideeLe { get; init; }
    public DateTimeOffset? ValideDu { get; init; }
    public DateTimeOffset? ValideAu { get; init; }

    /// <summary>Écrire la règle en brouillon plutôt que validée.</summary>
    public bool Brouillon { get; init; }

    /// <summary>
    /// Chemin du fichier CSV à écrire, pour une liste qu'on confie à quelqu'un.
    /// </summary>
    /// <remarks>
    /// La seule écriture de fichier que fasse une commande de lecture, et elle
    /// est hors de Sage : un tableau à remplir, qui reviendra saisi à la main
    /// dans Sage. Rien n'en revient automatiquement.
    /// </remarks>
    public string? Export { get; init; }
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
        string? article = null;
        string? famille = null;
        string? client = null;
        string? code = null;
        string? regime = null;
        string? fondement = null;
        string? validePar = null;
        string? empreinte = null;
        string? export = null;
        DateTimeOffset? valideeLe = null;
        DateTimeOffset? valideDu = null;
        DateTimeOffset? valideAu = null;
        var brouillon = false;
        var transmise = false;
        DateTimeOffset? quand = null;
        int? codeHttp = null;
        var nonCertifiee = false;
        var sansReference = false;
        var sansReferenceActuelle = false;
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
                case "dry-run":
                case "dryrun":
                    // Le verbe par défaut porte enfin son nom. Il n'en avait
                    // aucun, et le mot que tout le monde emploie pour le
                    // désigner tombait dans les numéros de pièce.
                    verbe = Verbe.DryRun;
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
                case "zero-vat-regle":
                case "regle-tva-zero":
                    verbe = Verbe.ZeroVatRegle;
                    break;
                case "--code":
                    code = Valeur() ?? "";
                    if (code is "") erreurs.Add("--code attend Tvac, Tvad ou Unknown.");
                    break;
                case "--regime":
                    regime = Valeur() ?? "";
                    if (regime is "") erreurs.Add("--regime attend TEE ou RME.");
                    break;
                case "--fondement":
                    fondement = Valeur() ?? "";
                    if (fondement is "") erreurs.Add("--fondement attend un fondement juridique.");
                    break;
                case "--valide-par":
                    validePar = Valeur() ?? "";
                    if (validePar is "") erreurs.Add("--valide-par attend qui a validé la règle.");
                    break;
                case "--valide-le":
                    if (DateTimeOffset.TryParse(Valeur(), out var le)) valideeLe = le;
                    else erreurs.Add("--valide-le attend une date, par exemple 2026-09-01.");
                    break;
                case "--empreinte":
                    empreinte = Valeur() ?? "";
                    if (empreinte is "") erreurs.Add("--empreinte attend l'empreinte du justificatif.");
                    break;
                case "--valide-du":
                    if (DateTimeOffset.TryParse(Valeur(), out var du)) valideDu = du;
                    else erreurs.Add("--valide-du attend une date.");
                    break;
                case "--valide-au":
                    if (DateTimeOffset.TryParse(Valeur(), out var au)) valideAu = au;
                    else erreurs.Add("--valide-au attend une date.");
                    break;
                case "--brouillon":
                    brouillon = true;
                    break;
                case "ncc":
                case "campagne-ncc":
                    verbe = Verbe.Ncc;
                    // Un NCC manquant se compte sur tout le dossier, pas sur un
                    // échantillon : la campagne se chiffre en appels à passer.
                    if (limite == LimiteParDefaut) limite = 2000;
                    break;
                case "--export":
                    export = Valeur() ?? "";
                    if (export is "") erreurs.Add("--export attend un chemin de fichier.");
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
                case "--article":
                    article = Valeur() ?? "";
                    if (article is "") erreurs.Add("--article attend une référence, par exemple 25SN001.");
                    break;
                case "--famille":
                    famille = Valeur() ?? "";
                    if (famille is "") erreurs.Add("--famille attend un code famille, par exemple 01.");
                    break;
                case "--client":
                    client = Valeur() ?? "";
                    if (client is "") erreurs.Add("--client attend un compte tiers, par exemple 4111SOGEL.");
                    break;
                case "--code-http":
                    if (int.TryParse(Valeur(), out var http) && http is >= 100 and <= 599) codeHttp = http;
                    else erreurs.Add("--code-http attend un code entre 100 et 599.");
                    break;
                case "--transmise":
                case "--au-portail":
                    transmise = true;
                    break;
                case "--sans-reference":
                case "--sans-référence":
                    sansReference = true;
                    break;
                case "--sans-reference-actuelle":
                case "--sans-référence-actuelle":
                    // « Le registre n'en porte aucune », a distinguer de
                    // --sans-reference, qui declare « certifiee sans numero ».
                    // Deux phrases voisines, deux sens : leur donner le meme
                    // drapeau serait la confusion d'etiquette que ce projet
                    // paie assez cher par ailleurs.
                    sansReferenceActuelle = true;
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
                    // Volontairement non filtrée par Marqueur() : c'est
                    // l'option qui sert à corriger une écriture fautive, et il
                    // faut donc pouvoir y nommer le mot fautif lui-même - y
                    // compris « LA_REFERENCE », qui a bel et bien été inscrit.
                    referenceActuelle = Valeur() ?? "";
                    if (referenceActuelle is "")
                        erreurs.Add("--reference-actuelle attend la référence que porte le registre aujourd'hui.");
                    break;
                case "--motif":
                    motif = Valeur() ?? "";
                    if (motif is "") erreurs.Add("--motif attend une phrase expliquant la décision.");
                    else if (Marqueur(motif, "--motif") is { } motifRefuse) erreurs.Add(motifRefuse);
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
                    else if (Marqueur(reference, "--reference") is { } refuse) erreurs.Add(refuse);
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
                    if (argument.StartsWith('-'))
                    {
                        erreurs.Add($"Option inconnue : {argument}");
                    }
                    else if (!argument.Any(char.IsDigit))
                    {
                        // Un mot nu sans le moindre chiffre n'est pas un numéro
                        // de pièce. Sans ce refus, « dry-run » - une commande
                        // qui n'existe pas - devenait un filtre sur la pièce
                        // nommée « dry-run », et le CLI répondait « Aucune
                        // facture ». Cette phrase se lit comme un fait sur le
                        // dossier : on en conclut que Sage est vide.
                        erreurs.Add(
                            $"« {argument} » n'est ni une commande connue ni un numéro de pièce " +
                            "(un numéro de pièce porte au moins un chiffre). Sans commande, " +
                            "c'est le dry run qui s'exécute.");
                    }
                    else
                    {
                        pieces.Add(argument);
                    }
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
            SansReferenceActuelle = sansReferenceActuelle,
            Jeton = jeton,
            Motif = motif,
            ReferenceActuelle = referenceActuelle,
            Evenement = evenement,
            Article = article,
            Code = code,
            Regime = regime,
            Fondement = fondement,
            ValidePar = validePar,
            Empreinte = empreinte,
            ValideeLe = valideeLe,
            ValideDu = valideDu,
            ValideAu = valideAu,
            Brouillon = brouillon,
            Export = export,
            Famille = famille,
            Client = client,
            Quand = quand,
            CodeHttp = codeHttp,
            NonCertifiee = nonCertifiee,
            SansReference = sansReference,
            Transmise = transmise,
            SupprimerReference = supprimerReference,
            SupprimerJeton = supprimerJeton,
            AfficherJson = afficherJson,
            Erreurs = erreurs,
        };
    }

    /// <summary>
    /// Les mots que la documentation emploie comme trous à remplir.
    /// </summary>
    /// <remarks>
    /// Trois fois de suite, un marqueur écrit pour être remplacé a été collé
    /// tel quel : « &lt;numéro&gt; », « …ce que la commande ci-dessus a montré… »,
    /// puis « LA_REFERENCE ». La troisième a inscrit au registre une référence
    /// FNE qui n'existe pas — exactement ce que ce projet s'interdit.
    ///
    /// Ce n'est pas une faute de frappe à reprocher à qui la commet : un outil
    /// qui accepte comme valeur un mot que sa propre aide donne comme à
    /// remplacer est un outil mal fait. La liste ci-dessous est tirée des
    /// exemples du dépôt, et doit le rester : y ajouter un mot chaque fois
    /// qu'un exemple en introduit un.
    /// </remarks>
    private static readonly string[] MarqueursDeDocumentation =
    [
        "LA_REFERENCE", "TA_REFERENCE_FNE", "VOTRE_REFERENCE", "REFERENCE", "REF",
        "A_COMPLETER", "A_RENSEIGNER", "LE_NUMERO", "NUMERO", "XXX", "MOT_DE_PASSE",
    ];

    /// <summary>
    /// Refuse une valeur qui n'en est visiblement pas une.
    /// </summary>
    /// <remarks>
    /// Deux formes : un marqueur connu, et tout ce qui porte les signes
    /// typographiques d'un texte à remplacer — chevrons, points de suspension,
    /// guillemets français. Une vraie référence FNE n'en contient aucun.
    /// </remarks>
    private static string? Marqueur(string valeur, string option)
    {
        var nu = valeur.Trim();

        if (nu.IndexOfAny(['<', '>', '…', '«', '»']) >= 0)
        {
            return $"{option} a reçu « {nu} », qui porte des signes de texte à remplacer " +
                   "(chevrons, points de suspension). Ce n'est pas une valeur : remplacez-la " +
                   "par ce que vous avez réellement lu.";
        }

        if (MarqueursDeDocumentation.Contains(nu, StringComparer.OrdinalIgnoreCase))
        {
            return $"{option} a reçu « {nu} », qui est un mot employé dans la documentation " +
                   "pour marquer un trou à remplir, pas une valeur. Rien n'a été écrit. " +
                   "Reprenez la commande avec ce que le portail DGI affiche réellement.";
        }

        return null;
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
          dotnet run --project src/SageFne.Reader -- audit-tva-zero --article 25SN001
          dotnet run --project src/SageFne.Reader -- zero-vat-regle afficher
          dotnet run --project src/SageFne.Reader -- zero-vat-regle verifier
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

        Règles de TVA à 0 % — « zero-vat-regle » suivi de :
          afficher              les règles du registre, leur état et leur preuve
          verifier              ce qui bloquerait une certification
          article REF           déclarer une règle d'article
          famille CODE          déclarer une règle de famille
          client COMPTE         déclarer un régime d'acheteur, ou une règle de client
          dossier               déclarer la règle du dossier
          revoquer ID           retirer une règle sans effacer son histoire
        avec :
          --code Tvac|Tvad|Unknown    le code FNE envoyé — jamais un fondement
          --regime TEE|RME            pour un régime d'acheteur
          --fondement …               RegimeAcheteur, ExonerationLegaleProduit, Convention, AutreValide
          --valide-par "…"            qui a validé, obligatoire hors brouillon
          --reference "…"             la preuve : réponse DGI, attestation, convention
          --valide-le, --valide-du, --valide-au, --empreinte, --motif
          --brouillon                 écrire sans valider : la règle ne produira aucun code

        Restreindre l'affichage de l'audit — l'analyse, elle, reste entière :
          --article REF     une référence d'article ; ajoute le relevé de toutes ses ventes
          --famille CODE    une famille d'article
          --client COMPTE   un compte tiers

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
