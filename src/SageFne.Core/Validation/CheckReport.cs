namespace SageFne.Core.Validation;

public enum Severite
{
    Avertissement,
    Erreur,
}

/// <param name="Severite">Erreur : la facture ne peut pas partir en l'état.</param>
/// <param name="Code">Repère court, stable, pour retrouver le contrôle.</param>
public sealed record Constat(Severite Severite, string Code, string Message);

/// <summary>Ce que les contrôles ont trouvé sur une pièce.</summary>
public sealed class CheckReport
{
    private readonly List<Constat> _constats = [];

    public IReadOnlyList<Constat> Constats => _constats;
    public bool ContientDesErreurs => _constats.Any(c => c.Severite == Severite.Erreur);

    public void Erreur(string code, string message) =>
        _constats.Add(new Constat(Severite.Erreur, code, message));

    public void Avertir(string code, string message) =>
        _constats.Add(new Constat(Severite.Avertissement, code, message));
}
