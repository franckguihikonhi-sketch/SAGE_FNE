using SageFne.Reader.Models.Sage;

namespace SageFne.Reader.Validation;

/// <summary>
/// Ce qu'une pièce doit porter avant d'être traduite pour la DGI.
/// </summary>
public static class InvoiceValidator
{
    public static void Validate(
        SageDocumentHeader? entete,
        SageCustomer? client,
        IReadOnlyCollection<SageDocumentLine> lignes,
        string template,
        CheckReport rapport)
    {
        if (entete is null)
        {
            rapport.Erreur("PIECE_INTROUVABLE", "Aucune pièce trouvée pour ce numéro.");
            return;
        }

        if (string.IsNullOrWhiteSpace(entete.Piece))
        {
            rapport.Erreur("PIECE_VIDE", "DO_Piece est vide : la pièce ne peut pas être identifiée.");
        }

        if (client is null)
        {
            rapport.Erreur("CLIENT_INTROUVABLE", $"Aucun client au compte « {entete.Tiers} » dans F_COMPTET.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(client.Intitule))
            {
                rapport.Avertir("CLIENT_SANS_NOM", $"Le client {client.CtNum} n'a pas d'intitulé.");
            }

            // Ce que Sage ne porte pas ne s'invente pas : on le signale, et
            // c'est à l'exploitant de dire si la DGI l'exige.
            if (string.IsNullOrWhiteSpace(client.Email))
            {
                rapport.Avertir(
                    "CLIENT_SANS_EMAIL",
                    $"CT_EMail vide pour {client.CtNum} : clientEmail partira vide. " +
                    "Aucune adresse n'est inventée.");
            }

            if (string.IsNullOrWhiteSpace(client.Telephone))
            {
                rapport.Avertir(
                    "CLIENT_SANS_TELEPHONE",
                    $"CT_Telephone vide pour {client.CtNum} : clientPhone partira vide.");
            }

            // Le NCC identifie le client auprès de la DGI : une facture B2B
            // sans NCC sera refusée à la certification.
            if (template.Equals("B2B", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(client.Identifiant))
            {
                rapport.Erreur(
                    "NCC_MANQUANT",
                    $"CT_Identifiant vide pour {client.CtNum} : le NCC est obligatoire en B2B.");
            }
        }

        if (lignes.Count == 0)
        {
            rapport.Erreur("SANS_LIGNE", $"La pièce {entete.Piece} n'a aucune ligne dans F_DOCLIGNE.");
            return;
        }

        foreach (var ligne in lignes)
        {
            var ou = $"ligne {ligne.Ligne}";

            // AR_Ref peut être vide (article libre) ; la désignation, non :
            // c'est ce que la facture certifiée affichera.
            if (string.IsNullOrWhiteSpace(ligne.Designation))
            {
                rapport.Erreur("DESIGNATION_VIDE", $"{ou} : désignation absente.");
            }

            if (string.IsNullOrWhiteSpace(ligne.ArticleReference))
            {
                rapport.Avertir("ARTICLE_SANS_REFERENCE", $"{ou} : AR_Ref vide.");
            }

            if (ligne.Quantite <= 0m)
            {
                rapport.Erreur("QUANTITE_INVALIDE", $"{ou} : quantité de {ligne.Quantite}, attendue strictement positive.");
            }

            if (ligne.PrixUnitaire < 0m)
            {
                rapport.Erreur("PRIX_NEGATIF", $"{ou} : prix unitaire de {ligne.PrixUnitaire}.");
            }
        }
    }
}
