using SageFne.Core.Configuration;
using SageFne.Core.Validation;

namespace SageFne.Installeur;

/// <summary>Ce que l'installation a besoin de savoir.</summary>
/// <remarks>
/// Aucun secret n'a de valeur par défaut, et c'est délibéré : une chaîne de
/// connexion ou une clé d'API qui aurait un défaut finirait par être installée
/// telle quelle sur le poste d'un client.
/// </remarks>
public sealed record Demande
{
    /// <summary>Chaîne de connexion à la base Sage. Lecture seule côté SQL.</summary>
    public string ChaineSage { get; init; } = "";

    /// <summary>Clé d'API de la DGI, propre au contribuable.</summary>
    public string CleFne { get; init; } = "";

    public string PointDeVente { get; init; } = "";
    public string Etablissement { get; init; } = "";

    /// <summary>Test ou Production. Test par défaut, et c'est voulu.</summary>
    public bool Production { get; init; }

    // --- Le SaaS, facultatif ------------------------------------------------

    public string SupabaseUrl { get; init; } = "";
    public string SupabaseCle { get; init; } = "";
    public string Dossier { get; init; } = "";

    // --- Où les choses vivent ----------------------------------------------

    public string Destination { get; init; } = @"C:\SageFne\agent";
    public string Registre { get; init; } = @"C:\ProgramData\SageFne\certifications.json";
    public string Journaux { get; init; } = @"C:\ProgramData\SageFne\journaux";
    public string NomService { get; init; } = "SageFneAgent";

    /// <summary>Ne rien écrire : montrer ce qui serait fait.</summary>
    public bool Simulation { get; init; }

    /// <summary>Ne rien demander : échouer si une valeur manque.</summary>
    public bool Silencieux { get; init; }

    /// <summary>Retirer le service et les fichiers de ce poste.</summary>
    /// <remarks>
    /// Un produit livré chez un client doit savoir partir. Ce qu'il ne retire
    /// jamais, en revanche, c'est le registre des certifications : il est la
    /// seule preuve de ce qui a été déclaré à la DGI, et sa suppression n'est
    /// pas une décision d'installateur.
    /// </remarks>
    public bool Desinstaller { get; init; }

    public bool SaasDemande =>
        !MarqueurGabarit.Absent(SupabaseUrl)
        || !MarqueurGabarit.Absent(Dossier)
        || !MarqueurGabarit.Absent(SupabaseCle);
}

/// <summary>
/// Ce qui empêche d'installer, dit avant d'avoir rien touché.
/// </summary>
/// <remarks>
/// Toutes les vérifications se font <b>avant</b> la première écriture. Une
/// installation qui s'arrête au milieu laisse un poste dans un état que
/// personne n'a voulu — c'est arrivé au script PowerShell, qui effaçait les
/// variables machine puis échouait sur une clé absente.
/// </remarks>
public static class Controles
{
    public static IReadOnlyList<string> Empechements(Demande demande, bool agentEmbarque)
    {
        var manques = new List<string>();

        if (!agentEmbarque)
        {
            manques.Add(
                "Cet exécutable ne porte pas l'agent. Il a été compilé sans sa charge utile : " +
                "utilisez celui produit par la chaîne de publication, pas un binaire de développement.");
        }

        if (MarqueurGabarit.Absent(demande.ChaineSage))
        {
            manques.Add(
                "La chaîne de connexion Sage n'est pas renseignée. Le compte SQL doit être en " +
                "LECTURE SEULE : le middleware n'écrit jamais dans Sage, et le compte doit le " +
                "garantir même si le code se trompait.");
        }
        else if (!ServicesMiddleware.ConnexionRenseignee(demande.ChaineSage))
        {
            manques.Add(
                "La chaîne de connexion Sage porte encore un gabarit. Elle serait installée telle " +
                "quelle, et l'agent retomberait sur son jeu d'essai sans le dire.");
        }

        if (MarqueurGabarit.Absent(demande.CleFne))
        {
            manques.Add("La clé d'API FNE n'est pas renseignée.");
        }

        if (MarqueurGabarit.Absent(demande.PointDeVente))
        {
            manques.Add(
                "Le point de vente n'est pas renseigné. Aucun contrôle de pièce ne peut le voir : " +
                "une facture irréprochable partirait et la DGI répondrait « Establishment is " +
                "invalid ». C'est arrivé sur quatre pièces d'affilée.");
        }

        if (MarqueurGabarit.Absent(demande.Etablissement))
        {
            manques.Add("L'établissement n'est pas renseigné.");
        }

        // Le registre est la seule mémoire des certifications. Hors profil
        // utilisateur, sans quoi le service et le CLI en tiendraient deux, et
        // deux registres pour une seule vérité finissent en doublon chez la
        // DGI — ce qui s'est produit.
        if (DansUnProfil(demande.Registre))
        {
            manques.Add(
                $"Le registre « {demande.Registre} » est dans un profil utilisateur. Le service " +
                "tourne sous un autre compte : il y écrirait un second registre, et une facture " +
                "déjà certifiée repartirait.");
        }

        if (demande.SaasDemande)
        {
            foreach (var (valeur, nom) in new[]
            {
                (demande.SupabaseUrl, "l'adresse Supabase"),
                (demande.SupabaseCle, "la clé de service Supabase"),
                (demande.Dossier, "l'identifiant du dossier"),
            })
            {
                if (MarqueurGabarit.Absent(valeur))
                {
                    manques.Add(
                        $"Le SaaS est demandé mais {nom} manque ou reste au gabarit. " +
                        "Les trois valeurs vont ensemble : sans elles, le miroir resterait éteint.");
                }
            }
        }

        return manques;
    }

    /// <summary>
    /// Vrai quand le chemin vit dans un profil utilisateur Windows.
    /// </summary>
    /// <remarks>
    /// Écrit à la main plutôt que confié à l'API : ce contrôle juge des chemins
    /// Windows où qu'il s'exécute, y compris sur l'intégration continue sous
    /// Linux.
    /// </remarks>
    public static bool DansUnProfil(string chemin) =>
        chemin.Contains(@"\Users\", StringComparison.OrdinalIgnoreCase)
        || chemin.Contains(@"\Documents and Settings\", StringComparison.OrdinalIgnoreCase);
}
