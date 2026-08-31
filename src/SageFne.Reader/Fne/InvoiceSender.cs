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
        CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return new DeblocageResultat(
                false,
                "Une réconciliation demande la référence FNE relevée sur le portail ou sur le " +
                "PDF : --reference \"…\". Sans elle, la trace ne permettrait pas de retrouver la " +
                "facture chez la DGI, et n'aurait pas d'objet.");
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
            $"sur le portail DGI par l'exploitant, non observée par le middleware. Empreinte " +
            "inscrite : celle du document au moment de la réconciliation.";

        if (!confirme)
        {
            return new DeblocageResultat(
                false,
                $"Rien n'a été inscrit : la confirmation manque. Serait inscrit — référence " +
                $"{reference}, jeton « {(string.IsNullOrWhiteSpace(jeton) ? "aucun" : jeton)} », " +
                $"identité {conversion.Header.Identite}, empreinte {conversion.Empreinte}, " +
                "état Certified. " + attestation,
                ConfirmationManque: true);
        }

        var reconciliee = new CertifiedInvoice
        {
            Identite = conversion.Header.Identite,
            Piece = conversion.Header.Piece,
            ReferenceFne = reference.Trim(),
            Token = jeton?.Trim() ?? "",
            CertifieeLe = quand,
            Empreinte = conversion.Empreinte,
            Etat = EtatFne.Certified,
            Erreur = attestation,
        };

        await registre.RecordAsync(reconciliee, cancellation);
        logger.LogInformation(
            "Pièce {Piece} réconciliée manuellement sous {Reference}.", piece, reference);

        return new DeblocageResultat(
            true,
            $"La pièce {piece} est inscrite certifiée sous {reference}. Elle ne repartira plus.",
            EtatFne.Certified);
    }

    private static CertifiedInvoice Trace(InvoiceConversion conversion, EtatFne etat) => new()
    {
        Identite = conversion.Header.Identite,
        Piece = conversion.Header.Piece,
        CertifieeLe = DateTimeOffset.Now,
        Empreinte = conversion.Empreinte,
        Etat = etat,
    };
}
