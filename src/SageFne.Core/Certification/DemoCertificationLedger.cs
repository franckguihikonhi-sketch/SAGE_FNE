namespace SageFne.Core.Certification;

/// <summary>
/// Registre d'essai, en mémoire, pour le dry run hors base.
/// </summary>
/// <remarks>
/// Il déclare la pièce 1219 déjà certifiée et inchangée, et la pièce 1220
/// certifiée puis modifiée depuis : les deux états qu'un registre vide ne
/// montrerait pas.
/// </remarks>
public sealed class DemoCertificationLedger : ICertificationLedger
{
    private readonly Dictionary<string, CertifiedInvoice> _registre = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// L'empreinte de la 1219 est calculée au démarrage sur la traduction
    /// réelle : le jeu d'essai reste vrai même si le mapping évolue.
    /// </summary>
    public void MarquerCertifiee(string identite, string piece, string empreinte, DateTimeOffset quand) =>
        _registre[identite] = new CertifiedInvoice
        {
            Identite = identite,
            Piece = piece,
            ReferenceFne = $"2304903U26{piece.PadLeft(8, '0')}",
            Token = "jeu-d-essai",
            CertifieeLe = quand,
            Empreinte = empreinte,
        };

    public Task<IReadOnlyDictionary<string, CertifiedInvoice>> LookupAsync(
        IReadOnlyCollection<string> identites,
        CancellationToken cancellation = default) =>
        Task.FromResult<IReadOnlyDictionary<string, CertifiedInvoice>>(
            identites.Where(_registre.ContainsKey).ToDictionary(identite => identite, identite => _registre[identite]));

    public Task RecordAsync(CertifiedInvoice certification, CancellationToken cancellation = default)
    {
        _registre[certification.Identite] = certification;
        return Task.CompletedTask;
    }
}
