using System.Text.Json;
using System.Text.Json.Nodes;
using SageFne.Core.Validation;

namespace SageFne.Core.Configuration;

/// <summary>
/// Ce qu'une réinstallation doit conserver du poste, et ce qu'elle remplace.
/// </summary>
/// <remarks>
/// Deux incidents viennent de là, et tous deux sont passés inaperçus des jours
/// durant : <c>FenetreJours</c> est retombé de 30 à 7, et l'identité du dossier
/// — <c>PointOfSale</c>, <c>Establishment</c> — est revenue à « A_COMPLETER »,
/// ce qui a fait refuser <b>toutes</b> les factures par la DGI.
///
/// La règle qui en sort tient en une phrase : <b>un réglage qu'on cesse de
/// porter est un réglage perdu</b>. Ce qui est déjà sur le poste l'emporte sur
/// ce que la livraison apporte, sauf si l'installateur le remplace
/// explicitement.
///
/// Deux exceptions, et elles sont énumérées, jamais devinées :
/// <list type="bullet">
/// <item>une valeur restée au gabarit ne vaut pas mieux que rien ;</item>
/// <item>les chemins que l'installation vient justement de fixer.</item>
/// </list>
///
/// Fonction pure : elle prend deux textes JSON et en rend un troisième. C'est
/// ce qui permet de l'éprouver sans machine Windows, sans service et sans
/// registre.
/// </remarks>
public static class FusionReglages
{
    /// <summary>Les clés que l'installation impose, quoi qu'il y ait en place.</summary>
    private static readonly string[] Imposees =
    [
        "Fne:CertificationLedgerPath",
        "Agent:CheminJournal",
    ];

    /// <summary>
    /// Fond ce que le poste portait dans ce que la livraison apporte.
    /// </summary>
    /// <param name="livre">Le fichier de la version installée.</param>
    /// <param name="enPlace">Celui que le poste portait, ou null à la première installation.</param>
    /// <param name="imposes">Ce que l'installateur pose explicitement, en « Section:Cle ».</param>
    public static string Fondre(
        string livre,
        string? enPlace,
        IReadOnlyDictionary<string, string?>? imposes = null)
    {
        var resultat = JsonNode.Parse(string.IsNullOrWhiteSpace(livre) ? "{}" : livre)!.AsObject();

        // Un appsettings.json illisible n'arrête pas l'installation : le poste
        // repart sur les valeurs livrées, ce qui est un recul mais pas une
        // panne. Le taire serait pire — Illisible() le dit à l'appelant.
        //
        // L'exception était attrapée dans Illisible() et pas ici : une
        // installation sur un fichier abîmé se serait interrompue au lieu de
        // repartir. Trouvé par le test, pas par relecture.
        if (!string.IsNullOrWhiteSpace(enPlace))
        {
            try
            {
                if (JsonNode.Parse(enPlace) is JsonObject objet)
                {
                    Reprendre(resultat, objet, prefixe: "");
                }
            }
            catch (JsonException)
            {
                // Rien à reprendre : le fichier livré fait foi.
            }
        }

        foreach (var (chemin, valeur) in imposes ?? new Dictionary<string, string?>())
        {
            if (valeur is null) continue;
            Poser(resultat, chemin, valeur);
        }

        return resultat.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Vrai quand l'ancien fichier existait mais n'était pas lisible.</summary>
    public static bool Illisible(string? enPlace)
    {
        if (string.IsNullOrWhiteSpace(enPlace)) return false;

        try
        {
            return JsonNode.Parse(enPlace) is not JsonObject;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>
    /// Recopie l'ancien par-dessus le livré, section par section.
    /// </summary>
    /// <param name="prefixe">
    /// Le chemin « Section:Cle » construit au fur et à mesure. Passé
    /// explicitement plutôt que déduit du parent : c'est lui qui décide de ce
    /// qui est imposé, et une déduction qui ne vaudrait qu'à un seul niveau de
    /// profondeur serait juste par accident.
    /// </param>
    private static void Reprendre(JsonObject cible, JsonObject source, string prefixe)
    {
        foreach (var (nom, valeur) in source)
        {
            var chemin = prefixe == "" ? nom : $"{prefixe}:{nom}";

            if (valeur is JsonObject sousSource)
            {
                if (cible[nom] is not JsonObject sousCible)
                {
                    sousCible = [];
                    cible[nom] = sousCible;
                }

                Reprendre(sousCible, sousSource, chemin);
                continue;
            }

            if (valeur is null) continue;

            // Les chemins que l'installation fixe elle-même ne se reprennent
            // pas : c'est elle qui sait où le registre et le journal doivent
            // vivre sur ce poste.
            if (Imposees.Contains(chemin, StringComparer.Ordinal)) continue;

            if (valeur is JsonValue brut && brut.TryGetValue<string>(out var texte))
            {
                // Une valeur vide ou restée au gabarit ne vaut pas mieux que
                // celle qui est livrée : la reprendre réinstallerait le trou.
                if (MarqueurGabarit.Absent(texte)) continue;
            }

            cible[nom] = valeur.DeepClone();
        }
    }

    private static void Poser(JsonObject racine, string chemin, string valeur)
    {
        var morceaux = chemin.Split(':', StringSplitOptions.RemoveEmptyEntries);
        var courant = racine;

        for (var rang = 0; rang < morceaux.Length - 1; rang++)
        {
            if (courant[morceaux[rang]] is not JsonObject sous)
            {
                sous = [];
                courant[morceaux[rang]] = sous;
            }

            courant = sous;
        }

        courant[morceaux[^1]] = valeur;
    }
}
