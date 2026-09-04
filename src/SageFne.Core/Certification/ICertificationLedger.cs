namespace SageFne.Core.Certification;

/// <summary>
/// Registre des pièces déjà certifiées.
/// </summary>
/// <remarks>
/// Volontairement réduit à deux opérations : demander ce qui est connu d'un
/// lot, et inscrire une certification. Un fichier suffit aujourd'hui ; une
/// table dans une base à nous — jamais celle de Sage — prendra la place le
/// jour où plusieurs postes certifieront en parallèle.
/// </remarks>
public interface ICertificationLedger
{
    /// <summary>
    /// Ce que le registre connaît de ces documents, en une seule lecture.
    /// </summary>
    /// <param name="identites">
    /// Identités stables (<see cref="Models.Sage.SageDocumentHeader.Identite"/>),
    /// et non des numéros de pièce : une facture comptabilisée doit rester
    /// reconnue sous l'identité qu'elle avait avant de l'être.
    /// </param>
    Task<IReadOnlyDictionary<string, CertifiedInvoice>> LookupAsync(
        IReadOnlyCollection<string> identites,
        CancellationToken cancellation = default);

    /// <summary>Inscrit une certification. Sera appelé à l'étape d'envoi.</summary>
    Task RecordAsync(CertifiedInvoice certification, CancellationToken cancellation = default);
}
