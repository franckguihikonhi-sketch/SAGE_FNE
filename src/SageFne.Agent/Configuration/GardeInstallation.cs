namespace SageFne.Agent.Configuration;

/// <summary>
/// Ce qui doit être vrai avant qu'un service ait le droit de démarrer.
/// </summary>
/// <remarks>
/// Un service Windows ne tourne pas sous le compte de l'exploitant. Selon son
/// paramétrage il est <c>LocalSystem</c>, <c>NetworkService</c> ou un compte
/// dédié — et deux mécanismes que le CLI utilise sans y penser s'en trouvent
/// cassés :
///
/// <b>Les secrets utilisateur.</b> <c>dotnet user-secrets</c> écrit dans le
/// profil de celui qui tape la commande. Un service tournant sous
/// <c>LocalSystem</c> lit le profil <c>systemprofile</c> : il n'y trouve ni
/// chaîne Sage, ni clé d'API. Comme la lecture est facultative, il démarrerait
/// sans rien dire et ne ferait jamais rien.
///
/// <b>Le registre des certifications.</b> Son chemin par défaut passe par
/// <c>%APPDATA%</c>, qui dépend lui aussi du compte. L'agent écrirait donc son
/// registre <b>ailleurs</b> que le CLI — deux mémoires séparées pour une seule
/// vérité. Le CLI dirait « jamais envoyée » d'une facture que l'agent a
/// envoyée, et le doublon suivrait.
///
/// Ce garde-fou refuse de démarrer plutôt que de laisser l'un ou l'autre
/// arriver en silence.
/// </remarks>
public static class GardeInstallation
{
    /// <summary>Ce qui empêche l'agent de démarrer, ou une liste vide.</summary>
    /// <param name="chaineSage">Chaîne de connexion résolue.</param>
    /// <param name="cheminRegistreConfigure">
    /// <c>Fne:CertificationLedgerPath</c>, tel qu'il est écrit dans la
    /// configuration — et non le chemin par défaut, qui est justement le piège.
    /// </param>
    /// <param name="cleApi">La clé, dont seule la présence est regardée.</param>
    public static IReadOnlyList<string> Empechements(
        string chaineSage, string? cheminRegistreConfigure, string? cleApi)
    {
        var empechements = new List<string>();
        var sageConfigure = !string.IsNullOrWhiteSpace(chaineSage);

        if (!sageConfigure)
        {
            // Sans base réelle, l'agent tourne sur le jeu d'essai : il ne peut
            // rien certifier, et rien de ce qui suit ne s'applique.
            return empechements;
        }

        if (string.IsNullOrWhiteSpace(cheminRegistreConfigure))
        {
            empechements.Add(
                "Fne:CertificationLedgerPath n'est pas renseigné. Le chemin par défaut passe " +
                "par %APPDATA%, qui dépend du compte : le service écrirait son registre ailleurs " +
                "que le CLI, et deux registres pour une seule vérité finissent en doublon chez " +
                "la DGI. Indiquez un chemin absolu, hors de tout profil utilisateur — par " +
                "exemple C:\\ProgramData\\SageFne\\certifications.json.");
        }
        else if (!EstAbsolu(cheminRegistreConfigure))
        {
            empechements.Add(
                $"Fne:CertificationLedgerPath vaut « {cheminRegistreConfigure} », qui est " +
                "relatif. Un service ne démarre pas dans le dossier où vous croyez : donnez un " +
                "chemin absolu.");
        }
        else if (DansUnProfilUtilisateur(cheminRegistreConfigure))
        {
            empechements.Add(
                $"Fne:CertificationLedgerPath vaut « {cheminRegistreConfigure} », qui est dans " +
                "un profil utilisateur. Le service tournera sous un autre compte et n'y aura pas " +
                "accès — ou y écrira un second registre. Placez-le hors des profils, par exemple " +
                "sous C:\\ProgramData.");
        }

        if (string.IsNullOrWhiteSpace(cleApi))
        {
            empechements.Add(
                "Fne:ApiKey est absente. Les secrets utilisateur (dotnet user-secrets) sont liés " +
                "au profil de celui qui les a posés : un service ne les voit pas. Passez par une " +
                "variable d'environnement MACHINE — Fne__ApiKey — ou faites tourner le service " +
                "sous le compte qui porte les secrets.");
        }

        return empechements;
    }

    /// <summary>
    /// Vrai quand le chemin est absolu, à la manière de Windows ou d'Unix.
    /// </summary>
    /// <remarks>
    /// Écrit à la main plutôt que confié à <c>Path.IsPathRooted</c> : celui-ci
    /// répond selon la plateforme qui l'exécute, et dirait de « C:\ProgramData »
    /// qu'il est relatif sur une machine Linux. Or ce contrôle juge des chemins
    /// Windows, où qu'il tourne — y compris sur l'intégration continue.
    /// </remarks>
    private static bool EstAbsolu(string chemin) =>
        chemin.StartsWith('/')
        || chemin.StartsWith(@"\\", StringComparison.Ordinal)
        || (chemin.Length >= 3
            && char.IsLetter(chemin[0])
            && chemin[1] == ':'
            && chemin[2] is '\\' or '/');

    /// <summary>Vrai quand le chemin traverse un dossier de profil.</summary>
    /// <remarks>
    /// Comparaison sur le texte plutôt que sur le dossier résolu : au moment où
    /// ce contrôle s'exécute, le profil du service n'est pas celui de la
    /// personne qui a écrit le paramétrage — c'est tout le problème.
    /// </remarks>
    private static bool DansUnProfilUtilisateur(string chemin)
    {
        var normalise = chemin.Replace('/', '\\');

        return normalise.Contains("\\Users\\", StringComparison.OrdinalIgnoreCase)
               || normalise.Contains("\\Documents and Settings\\", StringComparison.OrdinalIgnoreCase)
               || normalise.Contains("\\systemprofile\\", StringComparison.OrdinalIgnoreCase);
    }
}
