using System.Text.Json;
using SageFne.Core.Configuration;

namespace SageFne.Core.Tests;

/// <summary>
/// Ce qu'une réinstallation conserve du poste.
/// </summary>
/// <remarks>
/// Deux incidents réels sont à l'origine de ces tests, et tous deux sont passés
/// inaperçus des jours durant : la fenêtre est retombée de 30 jours à 7, et
/// l'identité du dossier est revenue à « A_COMPLETER » — ce qui a fait refuser
/// TOUTES les factures par la DGI, sans qu'aucun message ne le dise.
///
/// La règle : un réglage qu'on cesse de porter est un réglage perdu.
/// </remarks>
public class FusionReglagesTests
{
    private const string Livre = """
        {
          "Agent": { "Mode": "Manual", "FenetreJours": 7, "StabiliteMinutes": 2 },
          "Fne": {
            "PointOfSale": "A_COMPLETER",
            "Establishment": "A_COMPLETER",
            "Environment": "Test",
            "CertificationLedgerPath": ""
          }
        }
        """;

    private static JsonElement Fondu(string? enPlace, Dictionary<string, string?>? imposes = null) =>
        JsonDocument.Parse(FusionReglages.Fondre(Livre, enPlace, imposes)).RootElement;

    [Fact]
    public void La_fenetre_du_poste_survit_a_une_republication()
    {
        // Elle est retombée de 30 à 7, et personne ne s'en est aperçu : les
        // factures de plus d'une semaine avaient simplement cessé d'être
        // candidates.
        var fondu = Fondu("""{ "Agent": { "FenetreJours": 30 } }""");

        Assert.Equal(30, fondu.GetProperty("Agent").GetProperty("FenetreJours").GetInt32());
    }

    [Fact]
    public void L_identite_du_dossier_survit_a_une_republication()
    {
        var fondu = Fondu("""{ "Fne": { "PointOfSale": "FISH-AFRIC", "Establishment": "FISH-AFRIC" } }""");

        Assert.Equal("FISH-AFRIC", fondu.GetProperty("Fne").GetProperty("PointOfSale").GetString());
        Assert.Equal("FISH-AFRIC", fondu.GetProperty("Fne").GetProperty("Establishment").GetString());
    }

    [Fact]
    public void Le_mode_Automatic_ne_retombe_pas_en_Manual()
    {
        // Une préparation de routine qui ramènerait le poste en Manual ferait
        // chercher longtemps pourquoi plus rien ne part.
        Assert.Equal("Automatic",
            Fondu("""{ "Agent": { "Mode": "Automatic" } }""")
                .GetProperty("Agent").GetProperty("Mode").GetString());
    }

    [Fact]
    public void Un_gabarit_en_place_ne_supplante_pas_ce_qui_est_livre()
    {
        // Reprendre « A_COMPLETER » du poste réinstallerait le trou.
        var fondu = Fondu("""{ "Fne": { "PointOfSale": "A_COMPLETER" } }""",
            new() { ["Fne:PointOfSale"] = "FISH-AFRIC" });

        Assert.Equal("FISH-AFRIC", fondu.GetProperty("Fne").GetProperty("PointOfSale").GetString());
    }

    [Fact]
    public void Une_valeur_vide_en_place_ne_supplante_rien()
    {
        var fondu = Fondu("""{ "Agent": { "Mode": "" } }""");

        Assert.Equal("Manual", fondu.GetProperty("Agent").GetProperty("Mode").GetString());
    }

    [Fact]
    public void Ce_que_l_installateur_impose_l_emporte_sur_le_poste()
    {
        var fondu = Fondu("""{ "Agent": { "Mode": "Manual" } }""",
            new() { ["Agent:Mode"] = "Automatic" });

        Assert.Equal("Automatic", fondu.GetProperty("Agent").GetProperty("Mode").GetString());
    }

    [Fact]
    public void Les_chemins_que_l_installation_fixe_ne_se_reprennent_pas()
    {
        // C'est l'installation qui sait où le registre doit vivre sur ce poste
        // — hors de tout profil utilisateur. Reprendre l'ancien chemin
        // ramènerait un registre sous %APPDATA%, que le service ne voit pas.
        var fondu = Fondu(
            """{ "Fne": { "CertificationLedgerPath": "C:\\Users\\Samuel\\AppData\\Roaming\\SageFne\\certifications.json" } }""",
            new() { ["Fne:CertificationLedgerPath"] = @"C:\ProgramData\SageFne\certifications.json" });

        Assert.Equal(@"C:\ProgramData\SageFne\certifications.json",
            fondu.GetProperty("Fne").GetProperty("CertificationLedgerPath").GetString());
    }

    [Fact]
    public void Une_section_que_la_livraison_ignore_est_conservee()
    {
        // La section Saas n'existe pas dans le fichier livré : sans reprise,
        // une réinstallation éteindrait le miroir en silence.
        var fondu = Fondu("""{ "Saas": { "Url": "https://x.supabase.co", "DossierId": "abc" } }""");

        Assert.Equal("https://x.supabase.co", fondu.GetProperty("Saas").GetProperty("Url").GetString());
    }

    [Fact]
    public void La_premiere_installation_prend_le_fichier_livre_tel_quel()
    {
        var fondu = Fondu(null);

        Assert.Equal("Manual", fondu.GetProperty("Agent").GetProperty("Mode").GetString());
        Assert.Equal(7, fondu.GetProperty("Agent").GetProperty("FenetreJours").GetInt32());
    }

    [Theory]
    [InlineData("pas du json")]
    [InlineData("[1, 2, 3]")]
    public void Un_ancien_fichier_illisible_se_signale_et_n_arrete_rien(string enPlace)
    {
        // Le poste repart sur les valeurs livrées : c'est un recul, pas une
        // panne. Mais le taire ferait chercher ailleurs.
        Assert.True(FusionReglages.Illisible(enPlace));
        Assert.Equal("Manual",
            Fondu(enPlace).GetProperty("Agent").GetProperty("Mode").GetString());
    }

    [Fact]
    public void Un_fichier_absent_n_est_pas_illisible() =>
        Assert.False(FusionReglages.Illisible(null));

    [Fact]
    public void Le_resultat_est_du_JSON_indente_et_relisible()
    {
        // Il sera relu par l'agent au démarrage, et par un humain le jour où
        // quelque chose ne marchera pas.
        var texte = FusionReglages.Fondre(Livre, """{ "Agent": { "FenetreJours": 30 } }""");

        Assert.Contains("\n", texte);
        Assert.Equal(30, JsonDocument.Parse(texte).RootElement
            .GetProperty("Agent").GetProperty("FenetreJours").GetInt32());
    }
}
