using SageFne.Agent.Configuration;

namespace SageFne.Agent.Tests;

/// <summary>
/// Ce qui doit être vrai avant qu'un service ait le droit de démarrer.
/// </summary>
/// <remarks>
/// Un service Windows ne tourne pas sous le compte de celui qui l'installe.
/// Les secrets utilisateur lui échappent, et surtout le registre des
/// certifications atterrirait dans un autre profil que celui du CLI : deux
/// mémoires pour une seule vérité, et le doublon suit.
///
/// Ces contrôles ne sont pas de la prudence : ils décrivent une panne qui
/// serait arrivée à la première installation, en silence.
/// </remarks>
public class GardeInstallationTests
{
    private const string Chaine = "Server=SRV;Database=SAGE;";
    private const string RegistrePartage = @"C:\ProgramData\SageFne\certifications.json";
    private const string Cle = "une-cle";

    [Fact]
    public void Un_parametrage_complet_laisse_demarrer()
    {
        Assert.Empty(GardeInstallation.Empechements(Chaine, RegistrePartage, Cle));
    }

    [Fact]
    public void Sans_base_reelle_rien_n_est_exige()
    {
        // L'agent tourne alors sur le jeu d'essai : il ne peut rien certifier,
        // donc aucun de ces pièges ne s'applique.
        Assert.Empty(GardeInstallation.Empechements("", null, null));
    }

    [Fact]
    public void Un_registre_non_configure_empeche_le_demarrage()
    {
        // Le pire des deux pièges. Le chemin par défaut passe par %APPDATA%,
        // qui dépend du compte : le service écrirait son registre ailleurs que
        // le CLI, chacun ignorant ce que l'autre a envoyé.
        var empechements = GardeInstallation.Empechements(Chaine, null, Cle);

        Assert.Single(empechements);
        Assert.Contains("CertificationLedgerPath", empechements[0]);
        Assert.Contains("doublon", empechements[0]);
    }

    [Fact]
    public void Un_registre_relatif_empeche_le_demarrage()
    {
        // Un service ne démarre pas dans le dossier qu'on croit.
        var empechements = GardeInstallation.Empechements(Chaine, @"data\registre.json", Cle);

        Assert.Contains(empechements, cause => cause.Contains("relatif"));
    }

    [Theory]
    [InlineData(@"C:\Users\Samuel\AppData\Roaming\SageFne\certifications.json")]
    [InlineData(@"C:/Users/Samuel/Desktop/registre.json")]
    [InlineData(@"C:\Windows\System32\config\systemprofile\AppData\Roaming\SageFne\reg.json")]
    public void Un_registre_dans_un_profil_empeche_le_demarrage(string chemin)
    {
        // Y compris le profil système : c'est précisément là que LocalSystem
        // écrirait, et personne n'irait l'y chercher.
        var empechements = GardeInstallation.Empechements(Chaine, chemin, Cle);

        Assert.Contains(empechements, cause => cause.Contains("profil utilisateur"));
    }

    [Fact]
    public void Une_cle_absente_empeche_le_demarrage_et_dit_pourquoi()
    {
        // Le message doit nommer la vraie cause — les secrets liés au profil —
        // sinon on cherche une clé mal saisie pendant une heure.
        var empechements = GardeInstallation.Empechements(Chaine, RegistrePartage, null);

        Assert.Single(empechements);
        Assert.Contains("user-secrets", empechements[0]);
        Assert.Contains("Fne__ApiKey", empechements[0]);
    }

    [Fact]
    public void Une_verification_n_exige_pas_de_cle()
    {
        // « --verifier » lit, décide et s'arrête. Réclamer une clé pour
        // éprouver une lecture Sage découragerait l'épreuve qu'on veut voir
        // faite avant toute installation.
        Assert.Empty(GardeInstallation.Empechements(
            Chaine, RegistrePartage, null, pourEnvoyer: false));
    }

    [Fact]
    public void Une_verification_exige_quand_meme_le_bon_registre()
    {
        // Lire le mauvais registre ferait dire « à certifier » d'une facture
        // déjà partie : la vérification mentirait sur le point qui compte.
        var empechements = GardeInstallation.Empechements(
            Chaine, null, null, pourEnvoyer: false);

        Assert.Single(empechements);
        Assert.Contains("CertificationLedgerPath", empechements[0]);
    }

    [Fact]
    public void Les_empechements_se_cumulent()
    {
        // Les annoncer un par un ferait recommencer l'installation trois fois.
        var empechements = GardeInstallation.Empechements(Chaine, null, null);

        Assert.Equal(2, empechements.Count);
    }

    [Fact]
    public void Une_cle_faite_d_espaces_ne_compte_pas_comme_presente()
    {
        Assert.NotEmpty(GardeInstallation.Empechements(Chaine, RegistrePartage, "   "));
    }
}
