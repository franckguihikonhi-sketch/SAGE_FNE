using Microsoft.Extensions.Logging;
using SageFne.Reader.Batch;
using SageFne.Reader.Certification;
using SageFne.Reader.Data;

namespace SageFne.Reader.Fne;

/// <param name="Etat">L'état final inscrit au registre.</param>
/// <param name="Message">Ce qu'il faut dire à l'exploitant.</param>
public sealed record EnvoiResultat(
    EtatFne Etat,
    string Message,
    InvoiceConversion? Conversion = null,
    FneSignResult? Reponse = null)
{
    public bool Reussi => Etat == EtatFne.Certified;
}

/// <summary>
/// Issue d'un déblocage manuel.
/// </summary>
/// <param name="Applique">L'inscription a bien eu lieu.</param>
/// <param name="Message">Ce qu'il faut dire à l'exploitant.</param>
/// <param name="ConfirmationManque">
/// Tout était en règle : seul <c>--confirmer</c> manquait. Distinguer ce cas
/// d'un refus évite de conseiller <c>--confirmer</c> là où il ne changerait rien.
/// </param>
public sealed record DeblocageResultat(
    bool Applique,
    string Message,
    EtatFne? Etat = null,
    bool ConfirmationManque = false);

/// <summary>
/// Envoie une facture à la certification, et n'oublie jamais qu'elle est partie.
/// </summary>
/// <remarks>
/// L'ordre des opérations est la seule protection contre le doublon. Le registre
/// est marqué <see cref="EtatFne.Sending"/> <b>avant</b> l'appel, et non après :
/// si la machine s'arrête entre les deux, la trace existe. Une pièce retrouvée
/// dans cet état signale un envoi dont l'issue est inconnue, à vérifier sur le
/// portail avant tout renvoi — la DGI l'a peut-être certifiée.
///
/// L'inverse — écrire seulement après la réponse — perdrait la facture en cas
/// de coupure, et un second envoi créerait un doublon irrattrapable.
/// </remarks>
public sealed class InvoiceSender(
    InvoiceBatchReader lecteur,
    ICertificationLedger registre,
    IFneApiClient client,
    ILogger<InvoiceSender> logger)
{
    public async Task<EnvoiResultat> EnvoyerAsync(
        string piece,
        bool confirme,
        CancellationToken cancellation = default)
    {
        var lot = await lecteur.ReadAsync(InvoiceQuery.Piece(piece), cancellation);
        var conversion = lot.Conversions.FirstOrDefault();

        if (conversion is null)
        {
            return new EnvoiResultat(EtatFne.Error, $"Aucune facture au numéro {piece}.");
        }

        if (conversion.Etat == EtatPiece.EnSuspens)
        {
            return new EnvoiResultat(
                EtatFne.Sending,
                $"La pièce {piece} porte un envoi dont l'issue est inconnue. Vérifiez sur le " +
                "portail DGI avant tout renvoi : si elle y figure, elle est déjà certifiée et " +
                "un second envoi créerait un doublon.",
                conversion);
        }

        if (conversion.Etat != EtatPiece.ACertifier)
        {
            return new EnvoiResultat(
                EtatFne.Error,
                $"La pièce {piece} est « {conversion.LibelleEtat} » : elle ne peut pas partir.",
                conversion);
        }

        if (conversion.Invoice is null)
        {
            return new EnvoiResultat(EtatFne.Error, $"La pièce {piece} n'a pas pu être traduite.", conversion);
        }

        if (!confirme)
        {
            return new EnvoiResultat(
                EtatFne.Ready,
                "Rien n'a été envoyé : la confirmation manque.",
                conversion);
        }

        // Trace avant l'appel : c'est elle qui évitera le doublon si la réponse
        // se perd. Si elle échoue, rien ne part — une facture certifiée dont
        // nous n'aurions aucune trace serait pire que pas de facture du tout.
        var enCours = Trace(conversion, EtatFne.Sending);
        try
        {
            await registre.RecordAsync(enCours, cancellation);
        }
        catch (Exception erreur) when (erreur is not OperationCanceledException)
        {
            logger.LogError(erreur, "Registre inaccessible : envoi abandonné avant tout appel.");
            return new EnvoiResultat(
                EtatFne.Error,
                $"Le registre des certifications n'a pas pu être écrit : {erreur.Message} " +
                "Rien n'a été envoyé à la DGI — sans trace, une facture certifiée serait " +
                "invisible et pourrait repartir en double.",
                conversion);
        }

        logger.LogInformation("Pièce {Piece} marquée Sending avant appel.", piece);

        var reponse = await client.SignAsync(conversion.Invoice, cancellation);

        if (!reponse.Reussi)
        {
            // Un délai dépassé ou une réponse illisible laisse un doute sur ce
            // que la DGI a enregistré : l'état reste Sending, qui interdit le
            // renvoi automatique. Un refus franc, lui, redevient une erreur.
            // Sans réponse, ou avec une réponse serveur (5xx), on ignore ce que
            // la plateforme a enregistré : elle a pu persister la facture avant
            // d'échouer. Un refus client (4xx) est net — la requête a été
            // rejetée, rien n'a été créé.
            var douteux = reponse.CodeHttp is null or >= 500
                          || (reponse.ReferenceFne is null && reponse.CodeHttp < 400);
            var etat = douteux ? EtatFne.Sending : EtatFne.Error;

            await registre.RecordAsync(
                enCours with { Etat = etat, Reponse = reponse.CorpsBrut, Erreur = reponse.Erreur ?? "" },
                cancellation);

            return new EnvoiResultat(
                etat,
                douteux
                    ? $"Issue inconnue : {reponse.Erreur} La pièce reste « Sending » et ne sera pas renvoyée " +
                      "automatiquement."
                    : $"Refusée : {reponse.Erreur}",
                conversion,
                reponse);
        }

        await registre.RecordAsync(
            enCours with
            {
                Etat = EtatFne.Certified,
                ReferenceFne = reponse.ReferenceFne ?? "",
                Token = reponse.Token ?? "",
                Reponse = reponse.CorpsBrut,
            },
            cancellation);

        return new EnvoiResultat(
            EtatFne.Certified,
            $"Certifiée sous {reponse.ReferenceFne}.",
            conversion,
            reponse);
    }

    /// <summary>
    /// Tranche le sort d'une pièce restée « en suspens », d'après ce que
    /// l'exploitant a lu sur le portail de la DGI.
    /// </summary>
    /// <remarks>
    /// Aucun appel n'est fait : nous n'avons pas de quoi interroger la
    /// plateforme sur une facture, et le supposer serait pire que de demander.
    /// L'exploitant doit dire ce qu'il a vu — <c>--non-certifiee</c> ou
    /// <c>--reference</c> — et rien n'est deviné à sa place.
    ///
    /// Le registre n'oublie rien : l'entrée en suspens est remplacée par une
    /// entrée qui porte la décision et sa date, jamais effacée.
    /// </remarks>
    public async Task<DeblocageResultat> DebloquerAsync(
        string piece,
        string? reference,
        bool nonCertifiee,
        bool confirme,
        CancellationToken cancellation = default)
    {
        if (nonCertifiee == (reference is not null))
        {
            return new DeblocageResultat(
                false,
                "Cherchez d'abord la pièce sur le portail de la DGI, puis dites ce que vous y avez " +
                "vu : --non-certifiee si elle n'y figure pas, --reference REF si elle y figure. " +
                "L'un ou l'autre, jamais les deux : ce choix ne peut pas être deviné.");
        }

        var lot = await lecteur.ReadAsync(InvoiceQuery.Piece(piece), cancellation);
        var conversion = lot.Conversions.FirstOrDefault();

        if (conversion is null)
        {
            return new DeblocageResultat(false, $"Aucune facture au numéro {piece}.");
        }

        var trace = conversion.Certification;

        if (trace is null)
        {
            return new DeblocageResultat(
                false,
                $"La pièce {piece} ne porte aucune trace d'envoi : il n'y a rien à débloquer.");
        }

        if (trace.Etat == EtatFne.Certified)
        {
            return new DeblocageResultat(
                false,
                $"La pièce {piece} est déjà certifiée au registre" +
                $"{(trace.ReferenceFne == "" ? "" : $" sous {trace.ReferenceFne}")} : " +
                "une certification ne se réécrit pas. Si elle est erronée, la correction passe " +
                "par un avoir.");
        }

        if (trace.Etat != EtatFne.Sending)
        {
            return new DeblocageResultat(
                false,
                $"La pièce {piece} est au registre en « {trace.Etat} », pas en suspens : " +
                "elle peut déjà repartir, rien ne la bloque.");
        }

        var partiLe = trace.CertifieeLe.ToLocalTime().ToString("dd/MM/yyyy à HH:mm");
        var decision = nonCertifiee
            ? $"Portail DGI consulté le {DateTimeOffset.Now:dd/MM/yyyy} : la pièce n'y figure pas. " +
              $"L'envoi du {partiLe} n'a rien certifié."
            : $"Portail DGI consulté le {DateTimeOffset.Now:dd/MM/yyyy} : la pièce y figure sous " +
              $"{reference}. L'envoi du {partiLe} avait abouti.";

        if (!confirme)
        {
            return new DeblocageResultat(
                false,
                $"Rien n'a été inscrit : la confirmation manque. {decision}",
                ConfirmationManque: true);
        }

        // L'entrée en suspens laisse place à la décision : l'état change, la
        // réponse d'origine et l'empreinte restent, pour que la trace de la
        // tentative survive à son classement.
        var classee = nonCertifiee
            ? trace with { Etat = EtatFne.Error, Erreur = decision }
            : trace with
            {
                Etat = EtatFne.Certified,
                ReferenceFne = reference ?? "",
                Erreur = decision,
            };

        await registre.RecordAsync(classee, cancellation);
        logger.LogInformation(
            "Pièce {Piece} débloquée manuellement en {Etat}.", piece, classee.Etat);

        return new DeblocageResultat(
            true,
            nonCertifiee
                ? $"La pièce {piece} redevient à certifier. Relancez « envoyer {piece} » quand elle " +
                  "sera prête."
                : $"La pièce {piece} est classée certifiée sous {reference}. Elle ne repartira pas.",
            classee.Etat);
    }

    /// <summary>
    /// Inscrit au registre une certification constatée hors du middleware.
    /// </summary>
    /// <remarks>
    /// Le rattrapage d'une trace perdue. Le registre est la seule mémoire d'une
    /// certification — Sage n'en porte aucune — et cette mémoire peut manquer :
    /// registre effacé, envoi passé par un autre outil, réponse perdue. Sans ce
    /// rattrapage, la facture repartirait à la DGI et y prendrait une seconde
    /// référence.
    ///
    /// Aucune API n'est appelée. La référence vient de l'exploitant, qui l'a
    /// relevée sur le portail ou sur le PDF : c'est lui qui atteste, et la trace
    /// le dit pour que personne ne la prenne plus tard pour un aller-retour
    /// automatique.
    ///
    /// L'empreinte inscrite est celle du document <b>tel qu'il est aujourd'hui</b>,
    /// et non celle du corps réellement envoyé, qui est perdu avec la trace. Si
    /// la pièce a changé dans Sage depuis sa certification, la réconciliation
    /// grave cette version-là et l'écart avec la facture certifiée devient
    /// invisible. C'est le prix du rattrapage, et il est dit à l'écran.
    /// </remarks>
    public async Task<DeblocageResultat> ReconcilierAsync(
        string piece,
        string? reference,
        string? jeton,
        bool confirme,
        bool sansReference = false,
        string? motif = null,
        CancellationToken cancellation = default)
    {
        var avecReference = !string.IsNullOrWhiteSpace(reference);

        if (avecReference == sansReference)
        {
            return new DeblocageResultat(
                false,
                sansReference
                    ? "--reference et --sans-reference se contredisent : choisissez ce que le " +
                      "portail montre réellement."
                    : "Dites ce que le portail montre : --reference \"…\" s'il publie une " +
                      "référence, --sans-reference s'il n'en publie aucune. L'absence de " +
                      "référence doit être constatée, jamais subie : sans ce choix explicite, " +
                      "une faute de frappe passerait pour un constat.");
        }

        if (sansReference && string.IsNullOrWhiteSpace(motif))
        {
            return new DeblocageResultat(
                false,
                "Une réconciliation sans référence demande un motif : --motif \"…\". C'est la " +
                "seule chose qui restera pour expliquer, dans six mois, pourquoi cette " +
                "certification ne porte aucun numéro.");
        }

        var lot = await lecteur.ReadAsync(InvoiceQuery.Piece(piece), cancellation);
        var conversion = lot.Conversions.FirstOrDefault();

        if (conversion is null)
        {
            return new DeblocageResultat(false, $"Aucune facture au numéro {piece} dans Sage.");
        }

        if (conversion.Certification is { Etat: EtatFne.Certified } dejaLa)
        {
            return new DeblocageResultat(
                false,
                $"La pièce {piece} est déjà certifiée au registre" +
                $"{(dejaLa.ReferenceFne == "" ? "" : $" sous {dejaLa.ReferenceFne}")} : " +
                "il n'y a rien à réconcilier, et une certification ne se réécrit pas.");
        }

        if (conversion.Empreinte == "")
        {
            return new DeblocageResultat(
                false,
                $"La pièce {piece} ne se traduit pas au format FNE aujourd'hui : son empreinte " +
                "ne peut pas être calculée, et la trace serait inexploitable. Corrigez d'abord " +
                "ce qui la bloque — « statut " + piece + " » les énumère.");
        }

        var quand = DateTimeOffset.Now;
        var attestation =
            $"Réconciliation manuelle du {quand:dd/MM/yyyy à HH:mm}. Certification constatée " +
            "sur le portail DGI par l'exploitant, non observée par le middleware. Empreinte " +
            "inscrite : celle du document au moment de la réconciliation." +
            (sansReference
                ? $" Aucune référence publiée par la plateforme — motif : {motif!.Trim()}"
                : "");

        if (!confirme)
        {
            return new DeblocageResultat(
                false,
                "Rien n'a été inscrit : la confirmation manque. Serait inscrit — référence " +
                $"« {(sansReference ? "aucune" : reference)} », " +
                $"jeton « {(string.IsNullOrWhiteSpace(jeton) ? "aucun" : jeton)} », " +
                $"identité {conversion.Header.Identite}, empreinte {conversion.Empreinte}, " +
                "état Certified. " + attestation,
                ConfirmationManque: true);
        }

        var reconciliee = new CertifiedInvoice
        {
            Identite = conversion.Header.Identite,
            Piece = conversion.Header.Piece,
            ReferenceFne = sansReference ? "" : reference!.Trim(),
            Token = jeton?.Trim() ?? "",
            CertifieeLe = quand,
            Empreinte = conversion.Empreinte,
            Etat = EtatFne.Certified,
            Source = SourceCertification.ReconciliationManuelle,
            Motif = attestation,
        };

        await registre.RecordAsync(reconciliee, cancellation);
        logger.LogInformation(
            "Pièce {Piece} réconciliée manuellement sous {Reference}.", piece, reference);

        return new DeblocageResultat(
            true,
            sansReference
                ? $"La pièce {piece} est inscrite certifiée, sans référence. Elle ne repartira plus."
                : $"La pièce {piece} est inscrite certifiée sous {reference}. Elle ne repartira plus.",
            EtatFne.Certified);
    }

    /// <summary>
    /// Retire d'une certification une référence qui n'en était pas une.
    /// </summary>
    /// <remarks>
    /// Une réconciliation manuelle repose sur la lecture d'un humain, et un
    /// humain se trompe : une valeur d'exemple a été inscrite telle quelle à la
    /// place d'une référence. La laisser serait pire que l'absence — elle
    /// désignerait chez la DGI une facture qui n'existe pas.
    ///
    /// La correction ne touche que la référence. L'état reste
    /// <see cref="EtatFne.Certified"/>, l'identité, l'empreinte et
    /// l'horodatage d'origine ne bougent pas : la pièce doit rester bloquée au
    /// renvoi, c'est tout l'enjeu. Le motif s'ajoute au précédent sans
    /// l'effacer, et une copie du registre est prise avant écriture.
    ///
    /// L'appelant déclare la référence qu'il s'attend à trouver. Si elle diffère,
    /// rien n'est écrit : le registre a peut-être changé depuis qu'il l'a lu, et
    /// corriger à l'aveugle une certification est précisément ce qu'il faut
    /// éviter.
    /// </remarks>
    public async Task<DeblocageResultat> CorrigerReferenceAsync(
        string piece,
        string? referenceAttendue,
        string? motif,
        bool supprimerJeton,
        bool confirme,
        CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(motif))
        {
            return new DeblocageResultat(
                false,
                "Une correction demande un motif : --motif \"…\". Corriger une certification " +
                "sans dire pourquoi laisserait un registre que personne ne saura relire.");
        }

        if (string.IsNullOrWhiteSpace(referenceAttendue))
        {
            return new DeblocageResultat(
                false,
                "Déclarez la référence que vous vous attendez à trouver : " +
                "--reference-actuelle \"…\". La correction refuse d'agir si le registre porte " +
                "autre chose — il a pu changer depuis que vous l'avez lu.");
        }

        var lot = await lecteur.ReadAsync(InvoiceQuery.Piece(piece), cancellation);
        var conversion = lot.Conversions.FirstOrDefault();

        if (conversion?.Certification is not { } trace)
        {
            return new DeblocageResultat(
                false,
                conversion is null
                    ? $"Aucune facture au numéro {piece} dans Sage."
                    : $"La pièce {piece} ne porte aucune trace au registre : il n'y a rien à corriger.");
        }

        if (trace.Etat != EtatFne.Certified)
        {
            return new DeblocageResultat(
                false,
                $"La pièce {piece} est au registre en « {trace.Etat} », pas en « Certified ». " +
                "Cette correction ne vise que les certifications : rien d'autre n'est touché.");
        }

        // Une référence lue dans la réponse de la DGI n'est pas une déclaration
        // humaine : elle ne se corrige pas, elle fait foi. Seule une
        // réconciliation manuelle repose sur une lecture, donc peut être fautive.
        if (trace.Source == SourceCertification.Inconnue)
        {
            return new DeblocageResultat(
                false,
                $"L'origine de la certification de la pièce {piece} n'est pas établie : cette " +
                "entrée a été écrite avant que le registre ne consigne sa source. Une " +
                "correction ne se fait pas à l'aveugle — établissez-la d'abord avec " +
                $"« reparer-source {piece} », qui dira ce que les preuves internes désignent.");
        }

        if (trace.Source != SourceCertification.ReconciliationManuelle)
        {
            return new DeblocageResultat(
                false,
                $"La référence de la pièce {piece} vient de la réponse de la DGI, lue par le " +
                "middleware : elle fait foi et ne se retire pas. Seule une réconciliation " +
                "manuelle peut être corrigée.");
        }

        if (!string.Equals(trace.ReferenceFne, referenceAttendue.Trim(), StringComparison.Ordinal))
        {
            return new DeblocageResultat(
                false,
                $"Le registre porte « {(trace.SansReference ? "aucune référence" : trace.ReferenceFne)} » " +
                $"pour la pièce {piece}, et non « {referenceAttendue.Trim()} ». Rien n'est écrit : " +
                "vérifiez avec « statut " + piece + " » ce qu'il contient réellement.");
        }

        var quand = DateTimeOffset.Now;
        var trace_ =
            $"Correction du {quand:dd/MM/yyyy à HH:mm} : référence « {trace.ReferenceFne} » retirée" +
            (supprimerJeton && trace.Token != "" ? $", jeton « {trace.Token} » retiré" : "") +
            $". La certification est conservée. Motif : {motif.Trim()}";

        if (!confirme)
        {
            return new DeblocageResultat(
                false,
                $"Rien n'a été écrit : la confirmation manque.\n" +
                $"  Serait retiré  référence « {trace.ReferenceFne} »" +
                (trace.Token == ""
                    ? ""
                    : supprimerJeton
                        ? $", jeton « {trace.Token} »"
                        : $"\n  Serait gardé   jeton « {trace.Token} » (ajoutez --supprimer-jeton pour l'ôter aussi)") +
                $"\n  Serait gardé   état Certified, identité {trace.Identite}, " +
                $"empreinte {trace.Empreinte}, certifiée le {trace.CertifieeLe.ToLocalTime():dd/MM/yyyy à HH:mm}" +
                $"\n  {trace_}",
                ConfirmationManque: true);
        }

        // La copie d'abord : elle ne sert à rien après coup.
        string? sauvegarde = null;
        if (registre is JsonCertificationLedger surFichier)
        {
            try
            {
                sauvegarde = await surFichier.SauvegarderAsync(cancellation);
            }
            catch (Exception erreur) when (erreur is IOException or UnauthorizedAccessException)
            {
                return new DeblocageResultat(
                    false,
                    $"Le registre n'a pas pu être sauvegardé : {erreur.Message} Rien n'a été " +
                    "corrigé — une correction sans copie préalable ne se défait pas.");
            }
        }

        var corrigee = (trace with
        {
            ReferenceFne = "",
            Token = supprimerJeton ? "" : trace.Token,
        }).AvecMotif(trace_);

        await registre.RecordAsync(corrigee, cancellation);
        logger.LogInformation(
            "Pièce {Piece} : référence retirée du registre. Sauvegarde : {Sauvegarde}",
            piece, sauvegarde ?? "aucune");

        return new DeblocageResultat(
            true,
            $"La référence a été retirée. La pièce {piece} reste certifiée et ne repartira pas." +
            (sauvegarde is null ? "" : $"\n  Sauvegarde : {sauvegarde}"),
            EtatFne.Certified);
    }

    /// <summary>
    /// Établit l'origine d'une certification que le registre ne qualifie pas.
    /// </summary>
    /// <remarks>
    /// Les entrées écrites avant que le registre ne consigne sa source se
    /// relisent « origine inconnue ». Ce n'est pas un détail d'affichage : les
    /// corrections sont réservées aux déclarations humaines, et une
    /// réconciliation manuelle qu'on ne sait plus reconnaître devient
    /// incorrigible.
    ///
    /// La requalification ne repose que sur des preuves internes à l'entrée, et
    /// ne conclut jamais qu'à la réconciliation manuelle : elle seule laisse une
    /// attestation textuelle sans ambiguïté. Déduire « réponse de la plateforme »
    /// d'une absence de preuve serait refaire l'erreur qui rend cette commande
    /// nécessaire.
    ///
    /// Rien d'autre ne bouge : ni l'état, ni l'identité, ni l'empreinte, ni
    /// l'horodatage, ni la référence — même fautive. Une chose à la fois.
    /// </remarks>
    public async Task<DeblocageResultat> ReparerSourceAsync(
        string piece,
        bool confirme,
        CancellationToken cancellation = default)
    {
        var lot = await lecteur.ReadAsync(InvoiceQuery.Piece(piece), cancellation);
        var conversion = lot.Conversions.FirstOrDefault();

        if (conversion?.Certification is not { } trace)
        {
            return new DeblocageResultat(
                false,
                conversion is null
                    ? $"Aucune facture au numéro {piece} dans Sage."
                    : $"La pièce {piece} ne porte aucune trace au registre : il n'y a rien à réparer.");
        }

        var diagnostic = SourceHeuristique.Diagnostiquer(trace);

        if (!diagnostic.Concluante)
        {
            return new DeblocageResultat(
                false,
                $"Source actuelle   {Nommer(trace.Source)}\n" +
                $"  Source proposée   aucune\n" +
                $"  Justification     {diagnostic.Justification}");
        }

        var quand = DateTimeOffset.Now;
        var note =
            $"Réparation du {quand:dd/MM/yyyy à HH:mm} : source « {trace.Source} » corrigée en " +
            $"« {diagnostic.Proposee} ». {diagnostic.Justification}";

        if (!confirme)
        {
            return new DeblocageResultat(
                false,
                $"Source actuelle   {Nommer(trace.Source)}\n" +
                $"  Source proposée   {Nommer(diagnostic.Proposee)}\n" +
                $"  Justification     {diagnostic.Justification}\n" +
                $"  Inchangé          état {trace.Etat}, identité {trace.Identite}, " +
                $"empreinte {trace.Empreinte}, " +
                $"certifiée le {trace.CertifieeLe.ToLocalTime():dd/MM/yyyy à HH:mm}, " +
                $"référence « {(trace.SansReference ? "aucune" : trace.ReferenceFne)} »\n" +
                "  Rien n'a été écrit : la confirmation manque.",
                ConfirmationManque: true);
        }

        string? sauvegarde = null;
        if (registre is JsonCertificationLedger surFichier)
        {
            try
            {
                sauvegarde = await surFichier.SauvegarderAsync(cancellation);
            }
            catch (Exception erreur) when (erreur is IOException or UnauthorizedAccessException)
            {
                return new DeblocageResultat(
                    false,
                    $"Le registre n'a pas pu être sauvegardé : {erreur.Message} Rien n'a été " +
                    "réparé — une écriture sans copie préalable ne se défait pas.");
            }
        }

        await registre.RecordAsync((trace with { Source = diagnostic.Proposee }).AvecMotif(note), cancellation);
        logger.LogInformation(
            "Pièce {Piece} : source requalifiée en {Source}. Sauvegarde : {Sauvegarde}",
            piece, diagnostic.Proposee, sauvegarde ?? "aucune");

        return new DeblocageResultat(
            true,
            $"La source est maintenant « {Nommer(diagnostic.Proposee)} ». Rien d'autre n'a " +
            "changé : la pièce reste certifiée et non renvoyable." +
            (sauvegarde is null ? "" : $"\n  Sauvegarde : {sauvegarde}"),
            trace.Etat);
    }

    /// <summary>Le nom d'une source, tel qu'un exploitant peut le lire.</summary>
    public static string Nommer(SourceCertification source) => source switch
    {
        SourceCertification.ReconciliationManuelle => "Réconciliation manuelle / portail DGI",
        SourceCertification.Middleware => "Réponse de la plateforme, lue par le middleware",
        SourceCertification.Import => "Import d'un registre antérieur",
        _ => "Inconnue — entrée antérieure au suivi de la source",
    };

    /// <remarks>
    /// La source est posée ici, explicitement, et non laissée au défaut : cette
    /// trace naît d'un envoi que le middleware a fait lui-même. Compter sur le
    /// défaut d'une énumération pour dire quelque chose de vrai est l'erreur qui
    /// a rendu une réconciliation manuelle indiscernable d'une réponse de la DGI.
    /// </remarks>
    private static CertifiedInvoice Trace(InvoiceConversion conversion, EtatFne etat) => new()
    {
        Identite = conversion.Header.Identite,
        Piece = conversion.Header.Piece,
        CertifieeLe = DateTimeOffset.Now,
        Empreinte = conversion.Empreinte,
        Etat = etat,
        Source = SourceCertification.Middleware,
    };
}
