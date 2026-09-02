using SageFne.Core.Models.Sage;
using SageFne.Core.Mapping;

namespace SageFne.Core.Validation;

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

        // Le portail n'offre que quatre types de facturation. Le paramétrage,
        // lui, accepte n'importe quelle chaîne : « B2B » avec une espace, ou
        // « BTB », partirait tel quel et serait certifié tel quel — ou refusé
        // sans qu'on sache pourquoi.
        if (!GabaritFne.Reconnu(template))
        {
            rapport.Erreur(
                "GABARIT_INCONNU",
                $"Fne:Template vaut « {template} », qui n'est pas un type de facturation " +
                $"connu. Attendu : {GabaritFne.Attendus}.");
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

            // La DGI l'exige, et c'est écrit. « PROCEDURE D'INTERFACAGE DES
            // ENTREPRISES PAR API », mai 2025, tableau des paramètres de
            // POST /external/invoices/sign : clientEmail, obligatoire.
            //
            // Ce n'était qu'un avertissement, faute de savoir. Les faits vont
            // dans le même sens : les deux factures certifiées de ce dossier
            // portaient une adresse, et la seule envoyée sans - la 1222 - n'a
            // jamais été certifiée. Laisser partir une pièce dont on sait
            // qu'elle sera refusée, c'est dépenser une tentative pour rien.
            if (string.IsNullOrWhiteSpace(client.Email))
            {
                rapport.Avertir(
                    "CLIENT_SANS_EMAIL",
                    $"CT_EMail vide pour {client.CtNum} : la DGI marque clientEmail " +
                    "OBLIGATOIRE dans sa procédure d'interfaçage. L'adresse se saisit dans " +
                    "Sage — aucune n'est inventée ici. Avertissement et non blocage : la " +
                    "plateforme a accepté une pièce sans adresse, la règle écrite et le " +
                    "comportement observé se contredisent.");
            }

            // Le portail marque « Téléphone du client » d'une étoile, et il
            // distingue : « Régime d'imposition » n'en porte pas. Une pièce sans
            // téléphone est donc bloquée, comme une pièce sans NCC.
            //
            // La 1052 est partie avec un téléphone renseigné : elle ne prouve
            // rien sur le cas vide. Si la DGI confirme un jour que l'API
            // l'accepte absent, c'est ici que cela redeviendra un avertissement.
            if (string.IsNullOrWhiteSpace(client.Telephone))
            {
                rapport.Erreur(
                    "CLIENT_SANS_TELEPHONE",
                    $"CT_Telephone vide pour {client.CtNum} : le téléphone du client est " +
                    "obligatoire sur le formulaire de la DGI. Il se saisit dans Sage.");
            }

            // Le NCC identifie le client auprès de la DGI : une facture B2B
            // sans NCC sera refusée à la certification.
            if (GabaritFne.ExigeNcc(template)
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
