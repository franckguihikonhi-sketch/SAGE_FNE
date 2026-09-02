using SageFne.Core.Certification;
using SageFne.Core.Models.Fne;
using SageFne.Core.Models.Sage;
using SageFne.Core.Validation;

namespace SageFne.Core.Batch;

/// <summary>Ce qu'il reste à faire d'une pièce.</summary>
public enum EtatPiece
{
    /// <summary>Traduite, contrôlée, prête à partir.</summary>
    ACertifier,

    /// <summary>Une erreur empêche de l'envoyer.</summary>
    Bloquee,

    /// <summary>Déjà certifiée et inchangée depuis : à ne pas renvoyer.</summary>
    DejaCertifiee,

    /// <summary>Certifiée, puis modifiée dans Sage : la certifiée ne dit plus le vrai.</summary>
    ModifieeDepuis,

    /// <summary>
    /// Un envoi est parti et son issue reste inconnue.
    /// </summary>
    /// <remarks>
    /// La DGI l'a peut-être certifiée. Renvoyer créerait un doublon
    /// irrattrapable : il faut vérifier sur le portail avant tout.
    /// </remarks>
    EnSuspens,

    /// <summary>
    /// Déposée au portail de la DGI, en attente du clic qui la certifiera.
    /// </summary>
    /// <remarks>
    /// Ce n'est pas un suspens : on sait où elle est. Elle ne repart pas pour
    /// autant — elle est déjà là-bas.
    /// </remarks>
    Transmise,
}

/// <summary>
/// Ce qu'une pièce est devenue : sa facture FNE quand elle a pu être
/// construite, et dans tous les cas le rapport des contrôles.
/// </summary>
public sealed class InvoiceConversion
{
    public required SageDocumentHeader Header { get; init; }
    public SageCustomer? Customer { get; init; }
    public required IReadOnlyList<SageDocumentLine> Lines { get; init; }
    public FneInvoice? Invoice { get; init; }
    public required CheckReport Report { get; init; }

    /// <summary>Empreinte de ce qui partirait, quand la traduction a abouti.</summary>
    public string Empreinte { get; init; } = "";

    /// <summary>Trace de certification, si le registre en connaît une.</summary>
    public CertifiedInvoice? Certification { get; init; }

    public EtatPiece Etat { get; init; }

    /// <summary>Vrai quand la pièce peut partir à la certification.</summary>
    public bool EstPrete => Etat == EtatPiece.ACertifier;

    public string LibelleEtat => Etat switch
    {
        EtatPiece.ACertifier => "à certifier",
        EtatPiece.DejaCertifiee => "déjà certifiée",
        EtatPiece.ModifieeDepuis => "modifiée depuis",
        EtatPiece.EnSuspens => "envoi en suspens",
        EtatPiece.Transmise => "au portail, en attente de clic",
        _ => "bloquée",
    };

    public decimal TotalHT => Lines.Sum(ligne => ligne.MontantHT);
    public decimal TotalTTC => Lines.Sum(ligne => ligne.MontantTTC);
}

/// <summary>Le lot dans son ensemble.</summary>
public sealed class InvoiceBatch
{
    public required IReadOnlyList<InvoiceConversion> Conversions { get; init; }

    /// <summary>Constats qui portent sur le lot, pas sur une pièce.</summary>
    public required IReadOnlyList<Constat> Constats { get; init; }

    public int Total => Conversions.Count;
    public int ACertifier => Compte(EtatPiece.ACertifier);
    public int Bloquees => Compte(EtatPiece.Bloquee);
    public int DejaCertifiees => Compte(EtatPiece.DejaCertifiee);
    public int ModifieesDepuis => Compte(EtatPiece.ModifieeDepuis);
    public int EnSuspens => Compte(EtatPiece.EnSuspens);
    public int Transmises => Compte(EtatPiece.Transmise);

    private int Compte(EtatPiece etat) => Conversions.Count(conversion => conversion.Etat == etat);
    public decimal TotalHT => Conversions.Sum(conversion => conversion.TotalHT);
    public decimal TotalTTC => Conversions.Sum(conversion => conversion.TotalTTC);
    public int Lignes => Conversions.Sum(conversion => conversion.Lines.Count);
}
