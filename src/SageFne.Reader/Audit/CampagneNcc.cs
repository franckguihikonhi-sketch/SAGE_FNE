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

    /// <summary>Ce qui manque à ce compte pour que ses factures puissent partir.</summary>
    public bool SansNcc { get; init; }
    public bool SansTelephone { get; init; }

    /// <summary>Les manques, nommés, pour une liste d'appels.</summary>
    public string Manques => (SansNcc, SansTelephone) switch
    {
        (true, true) => "NCC + tél.",
        (true, false) => "NCC",
        (false, true) => "tél.",
        _ => "—",
    };

    /// <summary>
    /// <c>CT_TypeNIF</c>, tel quel. Sage le porte, et personne ne le lisait.
    /// </summary>
    /// <remarks>
    /// Ce champ est censé distinguer les natures de tiers. S'il sépare
    /// réellement les entreprises des particuliers dans ce dossier, il répond à
    /// la question qui précède toute la campagne : un particulier n'a pas de
    /// NCC à donner, et sa facture ne relève pas du gabarit B2B. Aucune
    /// interprétation n'est faite ici : la valeur est montrée, à vous de dire
    /// ce qu'elle vaut dans ce dossier.
    /// </remarks>
    public short TypeNif { get; init; }

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
/// Un NCC qui ne ressemble pas aux autres du dossier.
/// </summary>
/// <remarks>
/// Un écart n'est pas une faute : ce n'est pas au middleware de dire quelle
/// forme un NCC doit avoir. C'est une comparaison entre ce qui est saisi ici et
/// ce que ce même dossier porte majoritairement — de quoi aller regarder la
/// fiche, rien de plus.
/// </remarks>
public sealed record NccEcart(string CtNum, string Intitule, string Ncc, string Observation);

/// <summary>
/// L'état de la campagne : ce qui manque, dans quel ordre le chercher, et ce
/// que porte déjà le dossier.
/// </summary>
public sealed record EtatCampagneNcc
{
    public int Factures { get; init; }

    /// <summary>
    /// Factures qu'il manque quelque chose — NCC, téléphone, ou les deux.
    /// </summary>
    /// <remarks>
    /// Les deux champs sont obligatoires côté DGI, et se saisissent sur la même
    /// fiche client. Les compter séparément ferait croire à deux campagnes ;
    /// c'est un seul passage par fiche, avec deux colonnes à remplir.
    /// </remarks>
    public int FacturesIncompletes { get; init; }
    public decimal MontantIncomplet { get; init; }

    // Ces deux-là comptaient les seuls NCC manquants et s'appelaient
    // « SansNcc ». Depuis que le téléphone bloque aussi, le nom aurait dit une
    // chose et la valeur une autre — la faute qui revient le plus souvent ici.

    /// <summary>Le détail des deux manques, pour savoir ce qu'on va chercher.</summary>
    public int ComptesSansNcc => Comptes.Count(compte => compte.SansNcc);
    public int ComptesSansTelephone => Comptes.Count(compte => compte.SansTelephone);
    public int ComptesSansLesDeux =>
        Comptes.Count(compte => compte.SansNcc && compte.SansTelephone);

    public IReadOnlyList<CompteSansNcc> Comptes { get; init; } = [];

    /// <summary>Comptes distincts portant un NCC renseigné.</summary>
    public int ComptesRenseignes { get; init; }

    public IReadOnlyList<FormeNcc> Formes { get; init; } = [];
    public IReadOnlyList<NccPartage> Partages { get; init; } = [];
    public IReadOnlyList<NccDouteux> Douteux { get; init; } = [];
    public IReadOnlyList<NccEcart> Ecarts { get; init; } = [];

    /// <summary>
    /// Vrai quand <c>CT_TypeNIF</c> vaut la même chose partout.
    /// </summary>
    /// <remarks>
    /// Un champ constant ne distingue rien. Le dire une fois vaut mieux que
    /// d'aligner soixante-quatorze fois la même valeur dans une colonne : une
    /// colonne de zéros se lit comme une donnée, alors que c'est une absence.
    /// </remarks>
    public bool TypeNifConstant =>
        Comptes.Count > 1 && Comptes.Select(compte => compte.TypeNif).Distinct().Count() == 1;

    /// <summary>La forme la plus portée du dossier, quand une se détache.</summary>
    public FormeNcc? FormeDominante =>
        Formes.Count > 0 && Formes[0].Comptes > 1 ? Formes[0] : null;

    public int FacturesCouvertes => Factures - FacturesIncompletes;

    /// <summary>
    /// Combien de comptes il faut renseigner pour couvrir cette part des
    /// <b>factures</b> en attente.
    /// </summary>
    /// <remarks>
    /// À ne pas confondre avec <see cref="ComptesPourMontant"/> : ce ne sont pas
    /// les mêmes comptes. Trois comptes de ce dossier portent 563 factures pour
    /// 137 millions ; cinq autres portent 71 factures pour un milliard. Annoncer
    /// « trois comptes suffisent » sous un tableau classé par montant laisse
    /// croire que ce sont les trois premières lignes. Ce n'en est aucune.
    /// </remarks>
    public int ComptesPour(decimal part) =>
        Combien(part, FacturesIncompletes, compte => compte.Factures);

    /// <summary>
    /// Combien de comptes il faut renseigner pour couvrir cette part du
    /// <b>montant</b> en attente.
    /// </summary>
    public int ComptesPourMontant(decimal part) =>
        Combien(part, MontantIncomplet, compte => compte.MontantTTC);

    /// <summary>Les comptes qui débloquent le plus de factures, quel qu'en soit le montant.</summary>
    public IReadOnlyList<CompteSansNcc> ParNombre =>
        Comptes.OrderByDescending(compte => compte.Factures)
               .ThenByDescending(compte => compte.MontantTTC)
               .ToList();

    private int Combien(decimal part, decimal total, Func<CompteSansNcc, decimal> mesure)
    {
        if (total <= 0) return 0;

        var vise = total * part;
        decimal cumul = 0;
        var comptes = 0;

        foreach (var compte in Comptes.OrderByDescending(mesure))
        {
            cumul += mesure(compte);
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

        static bool Incomplet(SageCustomer? fiche) =>
            Absent(fiche?.Identifiant) || string.IsNullOrWhiteSpace(fiche?.Telephone);

        var manquants = pieces
            .Where(entete => Incomplet(Fiche(entete.Tiers)))
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
                    TypeNif = fiche?.TypeNif ?? 0,
                    SansNcc = Absent(fiche?.Identifiant),
                    SansTelephone = string.IsNullOrWhiteSpace(fiche?.Telephone),
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
            FacturesIncompletes = manquants.Sum(compte => compte.Factures),
            MontantIncomplet = manquants.Sum(compte => compte.MontantTTC),
            Comptes = manquants,
            ComptesRenseignes = renseignes.Count,
            Formes = Formes(renseignes),
            Partages = Partages(facturesParCompte, renseignes),
            Douteux = Douteux(renseignes),
            Ecarts = Ecarts(renseignes),
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
    /// Ce qui s'écarte de la forme majoritaire du dossier.
    /// </summary>
    /// <remarks>
    /// Deux observations seulement, et toutes deux relatives : une valeur qui
    /// retrouve la forme majoritaire une fois les espaces ôtés — presque
    /// toujours une frappe — et une forme que ce seul compte porte, quand
    /// plusieurs en partagent une autre. Ni l'une ni l'autre ne dit « invalide » :
    /// ce dossier n'a que huit NCC saisis, et huit valeurs ne font pas une règle.
    /// </remarks>
    private static List<NccEcart> Ecarts(IReadOnlyList<SageCustomer> renseignes)
    {
        var formes = Formes(renseignes);
        if (formes.Count == 0) return [];

        var dominante = formes[0];
        if (dominante.Comptes < 2) return [];

        var ecarts = new List<NccEcart>();

        foreach (var client in renseignes)
        {
            var ncc = client.Identifiant.Trim();
            var gabarit = Gabarit(ncc);
            if (gabarit == dominante.Gabarit) continue;

            var sansEspaces = Gabarit(ncc.Replace(" ", ""));
            var porteurs = formes.First(forme => forme.Gabarit == gabarit).Comptes;

            var observation =
                sansEspaces == dominante.Gabarit
                    ? $"un espace près : « {ncc.Replace(" ", "")} » aurait la forme majoritaire du dossier"
                : porteurs == 1
                    ? $"forme « {gabarit} » portée par ce seul compte, quand {dominante.Comptes} " +
                      $"en partagent « {dominante.Gabarit} »"
                : null;

            if (observation is not null)
            {
                ecarts.Add(new NccEcart(client.CtNum, client.Intitule, ncc, observation));
            }
        }

        return ecarts.OrderBy(ecart => ecart.CtNum, StringComparer.OrdinalIgnoreCase).ToList();
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
