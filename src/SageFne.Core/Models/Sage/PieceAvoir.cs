namespace SageFne.Core.Models.Sage;

/// <summary>
/// Reconnaître une pièce qui corrige une facture, plutôt qu'une facture.
/// </summary>
/// <remarks>
/// Un avoir se passe couramment sous forme de facture — <c>DO_Type</c> 6 ou 7 —
/// avec des lignes en négatif. Le middleware la lit donc comme les autres, et
/// jusqu'ici la bloquait ligne par ligne sur « quantité strictement positive ».
///
/// Le blocage était juste, mais le motif trompeur : il laissait croire à une
/// erreur de saisie alors que la pièce est parfaitement correcte dans Sage et
/// relève simplement d'un autre chemin. La DGI n'a pas de vente négative — son
/// avoir est <c>POST /external/invoices/{id}/refund</c>, où l'identifiant est
/// celui qu'elle a elle-même attribué à la facture d'origine.
///
/// Un fait clair vaut mieux que douze motifs qui trompent : la pièce porte
/// désormais un seul constat, qui nomme la commande à employer.
/// </remarks>
public static class PieceAvoir
{
    /// <summary>
    /// Vrai quand la pièce corrige au lieu de facturer.
    /// </summary>
    /// <remarks>
    /// Deux façons de le constater, toutes deux relevées sur le dossier réel :
    /// aucune ligne n'est positive et l'une au moins est négative, ou le total
    /// des lignes est négatif.
    ///
    /// Une pièce <b>mixte</b> — des lignes positives, une ligne négative, un
    /// total positif — n'est pas un avoir : c'est une facture ordinaire dont
    /// une ligne pose problème, et les contrôles habituels doivent la signaler
    /// telle quelle. Confondre les deux masquerait une vraie erreur de saisie.
    /// </remarks>
    public static bool Est(IReadOnlyCollection<SageDocumentLine> lignes)
    {
        if (lignes.Count == 0) return false;

        var aucunePositive = lignes.All(ligne => ligne.Quantite <= 0m);
        var uneNegative = lignes.Any(ligne => ligne.Quantite < 0m);

        return (aucunePositive && uneNegative)
            || lignes.Sum(ligne => ligne.MontantHT) < 0m;
    }

    /// <summary>
    /// La référence portée par le document, quand toutes les lignes s'accordent.
    /// </summary>
    /// <remarks>
    /// <c>DO_Ref</c> est un champ libre : Sage n'impose rien de ce qu'on y met.
    /// Il porte souvent le numéro de la facture corrigée, et c'est la seule
    /// piste que le document offre — mais ce n'est qu'une piste. Elle est donc
    /// montrée à l'exploitant pour qu'il la reconnaisse, jamais employée pour
    /// déclencher un avoir : se tromper de facture d'origine annulerait la
    /// mauvaise, et un avoir ne s'annule pas.
    ///
    /// Rendue seulement si toutes les lignes portent la même valeur. Des lignes
    /// discordantes ne désignent rien.
    /// </remarks>
    public static string? ReferencePortee(IReadOnlyCollection<SageDocumentLine> lignes)
    {
        var references = lignes
            .Select(ligne => ligne.DocumentReference?.Trim() ?? "")
            .Where(reference => reference.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return references.Count == 1 ? references[0] : null;
    }
}
