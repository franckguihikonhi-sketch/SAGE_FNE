using System.Text.Json;
using SageFne.Core.Validation;

namespace SageFne.Installeur;

/// <summary>Ce qu'un poste porte déjà, lu avant d'écrire quoi que ce soit.</summary>
public sealed record IdentiteEnPlace(string PointDeVente, string Etablissement, string Registre)
{
    public bool Renseignee =>
        !MarqueurGabarit.Absent(PointDeVente) || !MarqueurGabarit.Absent(Etablissement);
}

/// <summary>
/// Reconnaître qu'un poste appartient déjà à un autre client.
/// </summary>
/// <remarks>
/// Le danger d'un déploiement multi-clients n'est pas technique. Chaque client
/// a son propre accès FNE — sa clé, son NCC, son point de vente — et rien ne
/// se partage. Installer la clé de l'un sur le poste de l'autre ferait partir
/// les factures sous le mauvais NCC, et une facture certifiée ne s'annule que
/// par un avoir.
///
/// Une réinstallation sur le même client est banale et doit rester silencieuse.
/// Une installation par-dessus un <b>autre</b> client ne l'est pas : ou bien on
/// s'est trompé de poste, ou bien le poste change de main — et dans les deux
/// cas la personne doit le dire tout haut, jamais le découvrir après.
/// </remarks>
public static class Reconnaissance
{
    /// <summary>Lit l'identité posée sur ce poste, sans rien juger.</summary>
    public static IdentiteEnPlace? Lire(string appsettings)
    {
        if (string.IsNullOrWhiteSpace(appsettings)) return null;

        try
        {
            var racine = JsonDocument.Parse(appsettings).RootElement;
            if (!racine.TryGetProperty("Fne", out var fne)) return null;

            return new IdentiteEnPlace(
                Texte(fne, "PointOfSale"), Texte(fne, "Establishment"), Texte(fne, "CertificationLedgerPath"));
        }
        catch (JsonException)
        {
            // Un fichier abîmé ne dit rien de l'identité. L'installation
            // continue sur les valeurs livrées, et le dit ailleurs.
            return null;
        }
    }

    /// <summary>
    /// L'avertissement à lire tout haut, ou null quand il n'y a rien à dire.
    /// </summary>
    public static string? Avertissement(IdentiteEnPlace? enPlace, Demande demande)
    {
        if (enPlace is null || !enPlace.Renseignee) return null;

        var memeIdentite =
            string.Equals(enPlace.PointDeVente, demande.PointDeVente, StringComparison.OrdinalIgnoreCase)
            && string.Equals(enPlace.Etablissement, demande.Etablissement, StringComparison.OrdinalIgnoreCase);

        if (memeIdentite) return null;

        return
            $"Ce poste porte DÉJÀ l'identité « {enPlace.PointDeVente} / {enPlace.Etablissement} », " +
            $"et vous installez « {demande.PointDeVente} / {demande.Etablissement} ».\n" +
            "  Chaque client a son propre accès FNE : si vous vous êtes trompé de poste, les " +
            "factures partiraient sous le mauvais NCC.\n" +
            $"  Le registre en place — {(enPlace.Registre == "" ? "non renseigné" : enPlace.Registre)} — " +
            "porte les certifications de l'ancien client. Mettez-le de côté avant de continuer, " +
            "et n'effacez rien : une facture certifiée ne s'annule que par un avoir.";
    }

    private static string Texte(JsonElement objet, string nom) =>
        objet.TryGetProperty(nom, out var valeur) && valeur.ValueKind == JsonValueKind.String
            ? valeur.GetString() ?? ""
            : "";
}
