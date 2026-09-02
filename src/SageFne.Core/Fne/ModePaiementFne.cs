namespace SageFne.Core.Fne;

/// <summary>Un mode de règlement de la DGI : son code d'API et son libellé.</summary>
public sealed record ModePaiement(string Code, string Libelle);

/// <summary>
/// Les six modes de règlement que la plateforme FNE accepte.
/// </summary>
/// <remarks>
/// Relevés dans l'annexe « Lexique » de la
/// <see href="https://www.fne.dgi.gouv.ci/documents/FNE-procedureapi.pdf">procédure
/// d'interfaçage par API de la DGI</see> (mai 2025), qui les énumère
/// exactement : <c>cash</c> espèce, <c>card</c> carte bancaire, <c>check</c>
/// chèque, <c>mobile-money</c>, <c>transfer</c> virement bancaire,
/// <c>deferred</c> à terme. Ce sont les six que propose aussi le formulaire du
/// portail.
///
/// <b>Le libellé n'est pas le code.</b> Le portail affiche « Virement » ;
/// l'API attend « transfer ». Envoyer le libellé ferait refuser la facture, et
/// c'est précisément le genre de confusion que cette classe existe pour rendre
/// impossible : la liste déroulante montre le libellé et transmet le code.
///
/// <b>Aucune valeur par défaut n'est déduite ici.</b> Le mode de règlement est
/// un fait commercial que Sage ne porte pas dans les colonnes que nous lisons.
/// Le supposer serait faire dire à une facture certifiée quelque chose que
/// personne n'a affirmé.
/// </remarks>
public static class ModePaiementFne
{
    public const string Especes = "cash";
    public const string CarteBancaire = "card";
    public const string Cheque = "check";
    public const string MobileMoney = "mobile-money";
    public const string Virement = "transfer";
    public const string ATerme = "deferred";

    /// <summary>Les six, dans l'ordre du formulaire de la DGI.</summary>
    public static IReadOnlyList<ModePaiement> Tous { get; } =
    [
        new(CarteBancaire, "Carte bancaire"),
        new(Cheque, "Chèque"),
        new(Especes, "Espèces"),
        new(MobileMoney, "Mobile money"),
        new(Virement, "Virement"),
        new(ATerme, "À terme"),
    ];

    /// <summary>Vrai si ce code est l'un des six que la DGI accepte.</summary>
    public static bool EstConnu(string? code) =>
        !string.IsNullOrWhiteSpace(code)
        && Tous.Any(mode => mode.Code.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Le libellé d'un code, ou le code lui-même s'il est inconnu.</summary>
    /// <remarks>
    /// Rendre le code brut plutôt qu'un libellé inventé : un code inconnu doit
    /// se voir, pas se déguiser en mode valide.
    /// </remarks>
    public static string Libelle(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";

        var nu = code.Trim();
        return Tous.FirstOrDefault(mode =>
            mode.Code.Equals(nu, StringComparison.OrdinalIgnoreCase))?.Libelle ?? nu;
    }

    /// <summary>
    /// Le code normalisé, ou null si la valeur n'est pas un mode connu.
    /// </summary>
    /// <remarks>
    /// Accepte aussi le libellé français, parce qu'il sera recopié depuis le
    /// portail tôt ou tard — c'est arrivé quatre fois avec d'autres valeurs.
    /// Mieux vaut le traduire que le laisser partir tel quel.
    /// </remarks>
    public static string? Normaliser(string? valeur)
    {
        if (string.IsNullOrWhiteSpace(valeur)) return null;

        var nu = valeur.Trim();

        var parCode = Tous.FirstOrDefault(mode =>
            mode.Code.Equals(nu, StringComparison.OrdinalIgnoreCase));
        if (parCode is not null) return parCode.Code;

        var parLibelle = Tous.FirstOrDefault(mode =>
            mode.Libelle.Equals(nu, StringComparison.OrdinalIgnoreCase)
            || SansAccent(mode.Libelle).Equals(SansAccent(nu), StringComparison.OrdinalIgnoreCase));

        return parLibelle?.Code;
    }

    private static string SansAccent(string valeur) => valeur
        .Replace("è", "e").Replace("é", "e").Replace("ê", "e")
        .Replace("È", "E").Replace("É", "E").Replace("Ê", "E")
        .Replace("à", "a").Replace("À", "A");
}
