using SageFne.Core.Models.Sage;

namespace SageFne.Core.Mapping;

/// <summary>
/// Prix unitaire net d'une ligne, remises comprises.
/// </summary>
/// <remarks>
/// FNE attend un prix unitaire et une quantité, et recalcule le total. Envoyer
/// le prix <b>brut</b> d'une ligne remisée ferait certifier un montant supérieur
/// à celui facturé au client : c'est un faux, et il ne se corrige que par un
/// avoir.
///
/// Sage porte trois remises en cascade, chacune avec sa valeur
/// (<c>DL_Remise0NREM_Valeur</c>) et son type (<c>DL_Remise0NREM_Type</c> :
/// 0 pour un pourcentage, 1 pour un montant). La valeur seule est ambiguë —
/// « 10 » peut vouloir dire dix pour cent ou dix francs.
///
/// Mais Sage a déjà fait le calcul : <c>DL_MontantHT</c> est le net de la ligne,
/// après remises. Le prix envoyé en est donc <b>déduit</b> plutôt que recalculé,
/// ce qui le rend exact quelle que soit la lecture du type. Le recalcul en
/// cascade sert alors de contrôle : s'il tombe sur autre chose que Sage, c'est
/// notre lecture des types qui est fausse, et il faut le savoir avant d'envoyer.
/// </remarks>
public static class RemiseMapping
{
    /// <summary>Écart admis entre le recalcul et le net de Sage, en francs CFA.</summary>
    public const decimal Tolerance = 1m;

    /// <param name="PrixUnitaireNet">Ce qui part à FNE, remises déduites.</param>
    /// <param name="RemiseUnitaire">Écart entre le brut et le net, par unité.</param>
    /// <param name="Concordante">
    /// Le recalcul en cascade retrouve le net de Sage. Faux quand la ligne n'est
    /// pas remisée n'a pas de sens : dans ce cas la propriété vaut vrai.
    /// </param>
    public sealed record Resultat(
        bool Remisee,
        decimal PrixUnitaireNet,
        decimal RemiseUnitaire,
        bool Concordante,
        string Description,
        IReadOnlyList<string> Avertissements);

    public static Resultat Read(SageDocumentLine ligne)
    {
        var remises = ligne.Remises().Where(remise => remise.Presente).ToList();

        if (remises.Count == 0)
        {
            return new Resultat(false, ligne.PrixUnitaire, 0m, true, "", []);
        }

        var avertissements = new List<string>();
        var description = string.Join(" puis ", remises.Select(remise => remise.Libelle));

        // 1. Ce que donnerait la cascade, selon notre lecture des types.
        var cascade = ligne.PrixUnitaire;
        foreach (var remise in remises)
        {
            switch (remise.Type)
            {
                case SageRemise.Pourcentage:
                    cascade *= 1m - remise.Valeur / 100m;
                    break;
                case SageRemise.Montant:
                    cascade -= remise.Valeur;
                    break;
                default:
                    avertissements.Add(
                        $"remise {remise.Rang} de type {remise.Type}, inconnu de la lecture : " +
                        "elle n'entre pas dans le recalcul.");
                    break;
            }
        }

        // 2. Ce que Sage a effectivement retenu. C'est cela qui fait foi.
        var netDeSage = ligne.Quantite != 0m && ligne.MontantHT != 0m
            ? ligne.MontantHT / ligne.Quantite
            : (decimal?)null;

        if (netDeSage is null)
        {
            // Sans quantité ni montant, rien à déduire : la cascade reste le
            // seul chiffre disponible, et elle n'est pas confirmée.
            avertissements.Add(
                "quantité ou DL_MontantHT à zéro : le prix net ne peut pas être " +
                "recoupé avec le montant calculé par Sage.");
            return new Resultat(
                true, cascade, ligne.PrixUnitaire - cascade, false, description, avertissements);
        }

        // 3. Les deux doivent se rejoindre. Sinon, notre lecture des types est
        //    fausse — le prix envoyé reste juste, mais le contrôle doit le dire.
        var ecart = Math.Abs(cascade - netDeSage.Value) * Math.Abs(ligne.Quantite);
        var concordante = ecart <= Tolerance && avertissements.Count == 0;

        if (!concordante && avertissements.Count == 0)
        {
            avertissements.Add(
                $"la cascade {description} sur {ligne.PrixUnitaire} donne {cascade}, " +
                $"alors que Sage retient {netDeSage.Value} ({ligne.MontantHT} / {ligne.Quantite}). " +
                "C'est le chiffre de Sage qui est envoyé.");
        }

        return new Resultat(
            true,
            netDeSage.Value,
            ligne.PrixUnitaire - netDeSage.Value,
            concordante,
            description,
            avertissements);
    }
}
