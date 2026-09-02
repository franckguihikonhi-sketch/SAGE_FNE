namespace SageFne.Core.Models.Sage;

/// <summary>
/// Client, lu dans F_COMPTET.
/// </summary>
/// <remarks>
/// Dans ce dossier, CT_Identifiant porte le NCC du client : c'est lui qui
/// alimente clientNcc côté FNE.
/// </remarks>
public sealed class SageCustomer
{
    public required string CtNum { get; init; }
    public string Intitule { get; init; } = "";
    /// <summary>NCC du client dans ce dossier.</summary>
    public string Identifiant { get; init; } = "";
    public string Adresse { get; init; } = "";
    public string Complement { get; init; } = "";
    public string CodePostal { get; init; } = "";
    public string Ville { get; init; } = "";
    public string Pays { get; init; } = "";
    public string Telephone { get; init; } = "";
    public string Email { get; init; } = "";
    public short TypeNif { get; init; }
}
