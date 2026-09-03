using System.Text.Json;
using SageFne.Core.Models.Fne;

namespace SageFne.Core.Fne;

/// <summary>
/// Une ligne certifiée, telle que la DGI l'a enregistrée.
/// </summary>
/// <param name="Id">L'identifiant attribué par la DGI, seul acceptable en avoir.</param>
/// <param name="Quantite">La quantité certifiée : le maximum qu'un avoir puisse rendre.</param>
/// <param name="Reference">La référence d'article, pour que l'exploitant reconnaisse la ligne.</param>
/// <param name="Designation">Le libellé, pour la même raison.</param>
public sealed record LigneCertifiee(
    string Id,
    decimal Quantite,
    string Reference,
    string Designation);

/// <summary>
/// Ce qu'une réponse de certification permet — ou non — d'annuler.
/// </summary>
/// <param name="IdFacture">L'identifiant à mettre dans l'URL de l'avoir.</param>
/// <param name="Lignes">Les lignes certifiées, avec leurs identifiants DGI.</param>
/// <param name="Empechement">Ce qui manque, quand l'avoir est impossible.</param>
public sealed record LectureAvoir(
    string IdFacture,
    IReadOnlyList<LigneCertifiee> Lignes,
    string? Empechement = null)
{
    public bool Possible => Empechement is null;
}

/// <summary>
/// Lit, dans la réponse de certification conservée au registre, ce que l'avoir
/// exige.
/// </summary>
/// <remarks>
/// L'avoir ne se construit pas à partir de Sage. La procédure de la DGI est
/// explicite : l'identifiant de l'URL « doit être récupéré dans la réponse de
/// la requête de certification », et chaque article de l'avoir porte l'<c>id</c>
/// que la plateforme lui a donné. Aucun de ces identifiants n'existe de notre
/// côté — nous ne pouvons que les relire.
///
/// C'est pourquoi <see cref="Certification.CertifiedInvoice.Reponse"/> conserve
/// le corps brut : la décision de le garder, prise pour instruire les envois
/// douteux, est ce qui rend l'avoir possible aujourd'hui.
///
/// Rien n'est deviné. Une réponse qui ne porte pas ces identifiants donne un
/// empêchement en clair, jamais un avoir approximatif : se tromper de ligne
/// annulerait la mauvaise, et un avoir ne s'annule pas non plus.
/// </remarks>
public static class AvoirFne
{
    public static LectureAvoir Lire(string? corpsReponse)
    {
        if (string.IsNullOrWhiteSpace(corpsReponse))
        {
            return Impossible(
                "le registre ne conserve aucune réponse pour cette pièce. Les identifiants " +
                "d'articles viennent de la DGI et n'existent nulle part ailleurs : sans cette " +
                "réponse, l'avoir ne peut pas être construit ici. Il reste à faire au portail.");
        }

        JsonElement racine;
        try
        {
            using var document = JsonDocument.Parse(corpsReponse);
            racine = document.RootElement.Clone();
        }
        catch (JsonException erreur)
        {
            return Impossible($"la réponse conservée n'est pas du JSON lisible : {erreur.Message}");
        }

        if (!Objet(racine, "invoice", out var facture))
        {
            // Les certifications antérieures à la lecture complète de la
            // procédure ont pu être enregistrées sur une réponse tronquée.
            return Impossible(
                "la réponse conservée ne porte pas d'objet « invoice ». C'est lui qui contient " +
                "l'identifiant de la facture et ceux de ses lignes. Le portail de la DGI reste " +
                "la voie pour cette pièce.");
        }

        if (!Texte(facture, "id", out var idFacture))
        {
            return Impossible(
                "« invoice.id » est absent de la réponse conservée. C'est l'identifiant que " +
                "l'URL de l'avoir réclame ; aucun autre champ ne le remplace.");
        }

        if (!facture.TryGetProperty("items", out var articles)
            || articles.ValueKind != JsonValueKind.Array)
        {
            return Impossible("« invoice.items » est absent de la réponse conservée.");
        }

        var lignes = new List<LigneCertifiee>();
        var rang = 0;

        foreach (var article in articles.EnumerateArray())
        {
            if (!Texte(article, "id", out var id))
            {
                return Impossible(
                    $"la ligne {rang} de la réponse conservée n'a pas d'« id ». Un avoir partiel " +
                    "sur les autres lignes serait un avoir faux : rien n'est envoyé.");
            }

            lignes.Add(new LigneCertifiee(
                id,
                Nombre(article, "quantity"),
                TexteOuVide(article, "reference"),
                TexteOuVide(article, "description")));
            rang++;
        }

        return lignes.Count == 0
            ? Impossible("la réponse conservée ne porte aucune ligne : il n'y a rien à annuler.")
            : new LectureAvoir(idFacture, lignes);
    }

    /// <summary>
    /// Le corps de l'avoir, pour tout ou partie des lignes certifiées.
    /// </summary>
    /// <remarks>
    /// <paramref name="quantites"/> est indexé par référence d'article. Une
    /// référence absente de la table rend la ligne en entier — c'est le cas
    /// courant, l'annulation complète.
    /// </remarks>
    public static CorpsAvoir Corps(
        IReadOnlyList<LigneCertifiee> lignes,
        IReadOnlyDictionary<string, decimal>? quantites = null) =>
        new([.. lignes
            .Select(ligne => new ArticleAvoir(
                ligne.Id,
                quantites is not null && quantites.TryGetValue(ligne.Reference, out var voulue)
                    ? voulue
                    : ligne.Quantite))
            .Where(article => article.Quantity > 0m)]);

    private static LectureAvoir Impossible(string pourquoi) => new("", [], pourquoi);

    private static bool Objet(JsonElement parent, string nom, out JsonElement valeur)
    {
        valeur = default;
        if (!parent.TryGetProperty(nom, out var trouve)) return false;
        if (trouve.ValueKind != JsonValueKind.Object) return false;
        valeur = trouve;
        return true;
    }

    private static bool Texte(JsonElement parent, string nom, out string valeur)
    {
        valeur = "";
        if (!parent.TryGetProperty(nom, out var trouve)) return false;
        if (trouve.ValueKind != JsonValueKind.String) return false;
        var lu = trouve.GetString();
        if (string.IsNullOrWhiteSpace(lu)) return false;
        valeur = lu;
        return true;
    }

    private static string TexteOuVide(JsonElement parent, string nom) =>
        Texte(parent, nom, out var valeur) ? valeur : "";

    private static decimal Nombre(JsonElement parent, string nom) =>
        parent.TryGetProperty(nom, out var trouve)
        && trouve.ValueKind == JsonValueKind.Number
        && trouve.TryGetDecimal(out var valeur)
            ? valeur
            : 0m;
}
