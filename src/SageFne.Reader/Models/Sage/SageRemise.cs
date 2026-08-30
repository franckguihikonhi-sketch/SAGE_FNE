namespace SageFne.Reader.Models.Sage;

/// <summary>
/// Une des trois remises d'une ligne : sa valeur et son type.
/// </summary>
/// <param name="Rang">1, 2 ou 3 — l'ordre d'application dans Sage.</param>
/// <param name="Valeur">DL_Remise0NREM_Valeur, sans unité tant que le type n'est pas lu.</param>
/// <param name="Type">DL_Remise0NREM_Type : 0 = pourcentage, 1 = montant.</param>
public readonly record struct SageRemise(int Rang, decimal Valeur, short Type)
{
    /// <summary>Type 0 dans Sage : la valeur est un pourcentage.</summary>
    public const short Pourcentage = 0;

    /// <summary>Type 1 dans Sage : la valeur est un montant.</summary>
    public const short Montant = 1;

    public bool Presente => Valeur != 0m;

    public string Libelle => Type switch
    {
        Pourcentage => $"{Valeur} %",
        Montant => $"{Valeur} (montant)",
        _ => $"{Valeur} (type {Type} inconnu)",
    };
}
