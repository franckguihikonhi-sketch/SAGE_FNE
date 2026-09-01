using SageFne.Reader.Models.Sage;

namespace SageFne.Reader.Audit;

/// <summary>
/// Un compte dont les factures attendent un NCC.
/// </summary>
/// <remarks>
/// Le montant compte autant que le nombre : dix petites factures et une grosse
/// ne se rappellent pas dans le même ordre. La date de la dernière facture
/// compte aussi — un client sans commande depuis deux ans se retrouve mal.
/// </remarks>
public sealed record CompteSansNcc
{
    public required string CtNum { get; init; }
    public string Intitule { get; init; } = "";

    public int Factures { get; init; }
    public decimal MontantTTC { get; init; }

    public DateTime PremiereFacture { get; init; }
    public DateTime DerniereFacture { get; init; }

    /// <summary>Ce qui permet d'aller chercher le NCC, quand la fiche le porte.</summary>
    public string Telephone { get; init; } = "";
    public string Email { get; init; } = "";
    public string Ville { get; init; } = "";

    /// <summary>Vrai quand la fiche client elle-même est introuvable.</summary>
    public bool FicheIntrouvable { get; init; }

    /// <summary>De quoi on dispose pour joindre ce client.</summary>
    public string MoyenDeContact => (Telephone, Email) switch
    {
        ("", "") => "— aucun —",
        (var tel, "") => tel,
        ("", var mail) => mail,
        var (tel, mail) => $"{tel} / {mail}",
    };
}

/// <summary>
/// Une forme de NCC observée dans le dossier, et son nombre de comptes.
/// </summary>
/// <remarks>
/// La longueur et la nature des caractères, pas un format décrété. Le middleware
/// n'a aucune autorité pour dire à quoi ressemble un NCC valide : il dit
/// seulement à quoi ressemblent ceux que ce dossier porte déjà. C'est ce qui
/// permet de reconnaître une saisie douteuse au retour de campagne, sans
/// refuser une forme légitime qu'on n'aurait pas prévue.
/// </remarks>
/// <param name="Gabarit">La forme, chiffres notés 9 et lettres notées A.</param>
public sealed record FormeNcc(string Gabarit, int Longueur, int Comptes, IReadOnlyList<string> Exemples);

/// <summary>
/// Deux comptes distincts portant le même NCC.
/// </summary>
/// <remarks>
/// Presque toujours un copier-coller. Les factures des deux comptes partiraient
/// alors sous un seul contribuable — celui dont le numéro a été recopié, qui
/// verrait apparaître chez lui des ventes qu'il n'a pas faites.
/// </remarks>
public sealed record NccPartage(string Ncc, IReadOnlyList<string> Comptes, int Factures);

/// <summary>
/// Un NCC présent mais qui n'en est visiblement pas un.
/// </summary>
public sealed record NccDouteux(string CtNum, string Intitule, string Ncc, string Pourquoi);

/// <summary>
/// L'état de la campagne : ce qui manque, dans quel ordre le chercher, et ce
/// que porte déjà le dossier.
/// </summary>
public sealed record EtatCampagneNcc
{
    public int Factures { get; init; }
    public int FacturesSansNcc { get; init; }
    public decimal MontantSansNcc { get; init; }

    public IReadOnlyList<CompteSansNcc> Comptes { get; init; } = [];

    /// <summary>Comptes distincts portant un NCC renseigné.</summary>
    public int ComptesRenseignes { get; init; }

    public IReadOnlyList<FormeNcc> Formes { get; init; } = [];
    public IReadOnlyList<NccPartage> Partages { get; init; } = [];
    public IReadOnlyList<NccDouteux> Douteux { get; init; } = [];

    public int FacturesCouvertes => Factures - FacturesSansNcc;

    /// <summary>
    /// Combien de comptes il faut renseigner pour couvrir cette part des
    /// factures qui en manquent.
    /// </summary>
    /// <remarks>
    /// C'est le chiffre qui décide d'une campagne : savoir que cinq appels
    /// valent mieux que soixante-quatorze change la manière de s'y prendre.
    /// </remarks>
    public int ComptesPour(decimal part)
    {
        if (FacturesSansNcc == 0) return 0;

        var vise = FacturesSansNcc * part;
        decimal cumul = 0;
        var comptes = 0;

        foreach (var compte in Comptes.OrderByDescending(compte => compte.Factures))
        {
            cumul += compte.Factures;
            comptes++;
            if (cumul >= vise) break;
        }

        return comptes;
    }
}

/// <summary>
/// Ce que le dossier dit de ses NCC — sans jamais en écrire un.
/// </summary>
/// <remarks>
/// Le NCC vit dans <c>F_COMPTET.CT_Identifiant</c>, et il s'y corrige. Le
/// middleware n'écrit rien dans Sage : cette analyse produit une liste d'appels
/// à passer, pas une mise à jour. Ce qui revient de la campagne se saisit dans
/// Sage, et se vérifie ici en relançant la commande.
/// </remarks>
public static class CampagneNcc
{
    /// <summary>Ce qu'on ne prend pas pour un NCC.</summary>
    private static readonly string[] Gabarits =
        ["A_COMPLETER", "A_RENSEIGNER", "TODO", "XXX", "NEANT", "RAS", "N/A", "NA", "-", "0"];

    /// <param name="entetes">Les pièces lues. Dédoublonnées par identité.</param>
    /// <param name="lignes">
    /// Les lignes de vente, seules à donner un montant fiable : dans ce dossier
    /// <c>DO_TotalTTC</c> vaut parfois 0, et un compte se classerait alors en
    /// queue de liste alors qu'il porte le plus gros du chiffre.
    /// </param>
    public static EtatCampagneNcc Analyser(
        IReadOnlyList<SageDocumentHeader> entetes,
        IReadOnlyList<SageDocumentLine> lignes,
        IReadOnlyDictionary<string, SageCustomer> clients)
    {
        // Une pièce passée de DO_Type 6 à 7 reste la même facture : la compter
        // deux fois gonflerait la campagne d'appels qui n'existent pas.
        var pieces = entetes
            .GroupBy(entete => entete.Identite, StringComparer.OrdinalIgnoreCase)
            .Select(groupe => groupe.First())
            .ToList();

        var montants = lignes
            .GroupBy(ligne => ligne.Piece, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                groupe => groupe.Key,
                groupe => groupe.Sum(ligne => ligne.MontantTTC),
                StringComparer.OrdinalIgnoreCase);

        SageCustomer? Fiche(string tiers) =>
            clients.TryGetValue(tiers, out var client) ? client : null;

        var manquants = pieces
            .Where(entete => Absent(Fiche(entete.Tiers)?.Identifiant))
            .GroupBy(entete => entete.Tiers, StringComparer.OrdinalIgnoreCase)
            .Select(groupe =>
            {
                var fiche = Fiche(groupe.Key);
                return new CompteSansNcc
                {
                    CtNum = groupe.Key,
                    Intitule = fiche?.Intitule ?? "",
                    Factures = groupe.Count(),
                    MontantTTC = groupe.Sum(entete => montants.GetValueOrDefault(entete.Piece)),
                    PremiereFacture = groupe.Min(entete => entete.Date),
                    DerniereFacture = groupe.Max(entete => entete.Date),
                    Telephone = fiche?.Telephone.Trim() ?? "",
                    Email = fiche?.Email.Trim() ?? "",
                    Ville = fiche?.Ville.Trim() ?? "",
                    FicheIntrouvable = fiche is null,
                };
            })
            // Le montant d'abord : c'est lui qui dit par quel appel commencer.
            .OrderByDescending(compte => compte.MontantTTC)
            .ThenByDescending(compte => compte.Factures)
            .ToList();

        // Seuls les comptes qui facturent : une fiche dormante n'apprend rien
        // sur la forme des NCC de ce dossier, et gonflerait les compteurs.
        var renseignes = pieces
            .Select(entete => Fiche(entete.Tiers))
            .Where(client => client is not null && !Absent(client.Identifiant))
            .GroupBy(client => client!.CtNum, StringComparer.OrdinalIgnoreCase)
            .Select(groupe => groupe.First()!)
            .ToList();

        var facturesParCompte = pieces
            .GroupBy(entete => entete.Tiers, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(groupe => groupe.Key, groupe => groupe.Count(), StringComparer.OrdinalIgnoreCase);

        return new EtatCampagneNcc
        {
            Factures = pieces.Count,
            FacturesSansNcc = manquants.Sum(compte => compte.Factures),
            MontantSansNcc = manquants.Sum(compte => compte.MontantTTC),
            Comptes = manquants,
            ComptesRenseignes = renseignes.Count,
            Formes = Formes(renseignes),
            Partages = Partages(facturesParCompte, renseignes),
            Douteux = Douteux(renseignes),
        };
    }

    /// <summary>
    /// Les formes que prennent les NCC déjà saisis, de la plus courante à la
    /// plus rare.
    /// </summary>
    private static List<FormeNcc> Formes(IReadOnlyList<SageCustomer> renseignes) =>
        renseignes
            .GroupBy(client => Gabarit(client.Identifiant.Trim()))
            .Select(groupe => new FormeNcc(
                groupe.Key,
                groupe.Key.Length,
                groupe.Count(),
                groupe.Take(3).Select(client => client.Identifiant.Trim()).ToList()))
            .OrderByDescending(forme => forme.Comptes)
            .ToList();

    /// <summary>Chiffres en 9, lettres en A, le reste tel quel.</summary>
    private static string Gabarit(string valeur) =>
        new(valeur.Select(caractere =>
            char.IsDigit(caractere) ? '9' : char.IsLetter(caractere) ? 'A' : caractere).ToArray());

    private static List<NccPartage> Partages(
        IReadOnlyDictionary<string, int> facturesParCompte, IReadOnlyList<SageCustomer> renseignes)
    {
        return renseignes
            .GroupBy(client => client.Identifiant.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(groupe => groupe.Select(client => client.CtNum)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(groupe =>
            {
                var comptes = groupe.Select(client => client.CtNum)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(compte => compte, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new NccPartage(
                    groupe.Key,
                    comptes,
                    comptes.Sum(compte => facturesParCompte.GetValueOrDefault(compte)));
            })
            .OrderByDescending(partage => partage.Factures)
            .ToList();
    }

    /// <summary>
    /// Les valeurs présentes qui n'ont pas l'air d'un NCC.
    /// </summary>
    /// <remarks>
    /// Trois signalements seulement, et chacun décrit un fait plutôt qu'un
    /// verdict de format : un gabarit de saisie, un numéro recopié du compte,
    /// une valeur si courte qu'elle ne peut identifier personne. Une forme
    /// inhabituelle mais légitime ne doit pas être refusée ici — c'est
    /// <see cref="Formes"/> qui la donne à lire, sans la juger.
    /// </remarks>
    private static List<NccDouteux> Douteux(IReadOnlyList<SageCustomer> renseignes)
    {
        var douteux = new List<NccDouteux>();

        foreach (var client in renseignes)
        {
            var ncc = client.Identifiant.Trim();

            var pourquoi =
                ncc.Equals(client.CtNum.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? "identique au numéro de compte Sage : le champ a servi à autre chose"
                : ncc.Distinct().Count() == 1
                    ? "un seul caractère répété"
                : ncc.Length < 4
                    ? $"{ncc.Length} caractère(s) : trop court pour identifier un contribuable"
                : null;

            if (pourquoi is not null)
            {
                douteux.Add(new NccDouteux(client.CtNum, client.Intitule, ncc, pourquoi));
            }
        }

        return douteux
            .OrderBy(entree => entree.CtNum, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Vrai quand la fiche ne porte pas de NCC exploitable.
    /// </summary>
    /// <remarks>
    /// Un gabarit non remplacé compte comme absent : « A_COMPLETER » partirait
    /// tel quel chez la DGI et serait certifié tel quel.
    /// </remarks>
    public static bool Absent(string? valeur) =>
        string.IsNullOrWhiteSpace(valeur)
        || Gabarits.Any(gabarit => valeur.Trim().Equals(gabarit, StringComparison.OrdinalIgnoreCase));
}
