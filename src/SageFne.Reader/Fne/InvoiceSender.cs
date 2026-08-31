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
        // se perd.
        var enCours = Trace(conversion, EtatFne.Sending);
        await registre.RecordAsync(enCours, cancellation);
        logger.LogInformation("Pièce {Piece} marquée Sending avant appel.", piece);

        var reponse = await client.SignAsync(conversion.Invoice, cancellation);

        if (!reponse.Reussi)
        {
            // Un délai dépassé ou une réponse illisible laisse un doute sur ce
            // que la DGI a enregistré : l'état reste Sending, qui interdit le
            // renvoi automatique. Un refus franc, lui, redevient une erreur.
            var douteux = reponse.CodeHttp is null || reponse.ReferenceFne is null && reponse.CodeHttp < 400;
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

    private static CertifiedInvoice Trace(InvoiceConversion conversion, EtatFne etat) => new()
    {
        Identite = conversion.Header.Identite,
        Piece = conversion.Header.Piece,
        CertifieeLe = DateTimeOffset.Now,
        Empreinte = conversion.Empreinte,
        Etat = etat,
    };
}
