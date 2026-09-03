namespace SageFne.Installeur;

/// <summary>Ce que l'installateur a tapé.</summary>
public sealed record Analyse(Demande Demande, IReadOnlyList<string> Erreurs, bool AideDemandee);

/// <summary>
/// Lecture des arguments.
/// </summary>
/// <remarks>
/// Tout ce qui n'est pas fourni se demande à l'écran, sauf en mode silencieux
/// — celui d'un déploiement scripté, où une question sans réponse bloquerait
/// une machine que personne ne regarde.
/// </remarks>
public static class LigneDeCommande
{
    public static Analyse Lire(string[] arguments)
    {
        var demande = new Demande();
        var erreurs = new List<string>();
        var aide = false;

        string? Valeur(ref int rang, string nom)
        {
            if (rang + 1 >= arguments.Length || arguments[rang + 1].StartsWith('-'))
            {
                erreurs.Add($"{nom} attend une valeur.");
                return null;
            }

            return arguments[++rang];
        }

        for (var rang = 0; rang < arguments.Length; rang++)
        {
            var mot = arguments[rang];

            switch (mot.ToLowerInvariant())
            {
                case "-h" or "--aide" or "--help" or "/?":
                    aide = true;
                    break;

                case "--sage":
                    demande = demande with { ChaineSage = Valeur(ref rang, "--sage") ?? "" };
                    break;
                case "--cle-fne":
                    demande = demande with { CleFne = Valeur(ref rang, "--cle-fne") ?? "" };
                    break;
                case "--point-de-vente":
                    demande = demande with { PointDeVente = Valeur(ref rang, "--point-de-vente") ?? "" };
                    break;
                case "--etablissement":
                    demande = demande with { Etablissement = Valeur(ref rang, "--etablissement") ?? "" };
                    break;

                case "--production":
                    demande = demande with { Production = true };
                    break;

                case "--supabase-url":
                    demande = demande with { SupabaseUrl = Valeur(ref rang, "--supabase-url") ?? "" };
                    break;
                case "--supabase-cle":
                    demande = demande with { SupabaseCle = Valeur(ref rang, "--supabase-cle") ?? "" };
                    break;
                case "--dossier":
                    demande = demande with { Dossier = Valeur(ref rang, "--dossier") ?? "" };
                    break;

                case "--destination":
                    demande = demande with { Destination = Valeur(ref rang, "--destination") ?? demande.Destination };
                    break;
                case "--registre":
                    demande = demande with { Registre = Valeur(ref rang, "--registre") ?? demande.Registre };
                    break;
                case "--journaux":
                    demande = demande with { Journaux = Valeur(ref rang, "--journaux") ?? demande.Journaux };
                    break;
                case "--service":
                    demande = demande with { NomService = Valeur(ref rang, "--service") ?? demande.NomService };
                    break;

                case "--simulation":
                    demande = demande with { Simulation = true };
                    break;
                case "--silencieux":
                    demande = demande with { Silencieux = true };
                    break;

                default:
                    erreurs.Add($"Option inconnue : {mot}");
                    break;
            }
        }

        return new Analyse(demande, erreurs, aide);
    }

    public const string Aide = """
        SageFneSetup - installe le middleware FNE sur un poste Windows.

        Lancez-le sans argument : il demandera ce qu'il lui faut.
        A executer en tant qu'administrateur.

          SageFneSetup.exe

        Pour un deploiement scripte, tout se passe en arguments :

          SageFneSetup.exe --silencieux ^
            --sage "Server=SRV;Database=BIJOU;User Id=lecteur_fne;Pwd=MOT_DE_PASSE" ^
            --cle-fne "VOTRE_CLE_DGI" ^
            --point-de-vente "FISH-AFRIC" --etablissement "FISH-AFRIC"

        Options

          --sage TEXTE             chaine de connexion Sage, compte en LECTURE SEULE
          --cle-fne TEXTE          cle d'API de la DGI, propre au contribuable
          --point-de-vente TEXTE   declare a la DGI, figure sur chaque facture
          --etablissement TEXTE    idem
          --production             viser la plateforme reelle (defaut : essai)

          --supabase-url TEXTE     adresse du projet, pour l'ecran distant
          --supabase-cle TEXTE     cle de service (secret)
          --dossier TEXTE          identifiant du dossier dans la base d'audit

          --destination CHEMIN     defaut C:\SageFne\agent
          --registre CHEMIN        defaut C:\ProgramData\SageFne\certifications.json
          --journaux CHEMIN        defaut C:\ProgramData\SageFne\journaux
          --service NOM            defaut SageFneAgent

          --simulation             montrer ce qui serait fait, sans rien ecrire
          --silencieux             ne rien demander ; echouer si une valeur manque

        Le service demarre en mode Manual : il observe et n'envoie rien tant
        qu'un humain n'a pas clique. Le passer en Automatic est une decision
        d'exploitation, prise apres avoir vu la liste.
        """;
}
