using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SageFne.Agent.Sante;
using SageFne.Agent.Surveillance;
using SageFne.Core.Batch;
using SageFne.Core.Fne;
using SageFne.Core.Data;
using SageFne.Core.Models.Sage;

namespace SageFne.Agent.Certification;

/// <summary>Ce qu'une certification demandée par un humain a donné.</summary>
public sealed record IssueCertification(
    bool Reussi,
    string Message,
    EtatFne? Etat = null,
    string ReferenceFne = "",
    int? CodeHttp = null,
    string ReponsePlateforme = "",
    string Identite = "");

/// <summary>
/// Le chemin unique par lequel une certification demandée à la main passe.
/// </summary>
/// <remarks>
/// L'interface existe pour que ce qui l'appelle puisse être éprouvé sans
/// joindre la DGI. Il n'y a qu'une implémentation, et il ne doit y en avoir
/// qu'une : deux chemins d'envoi, c'est une règle qui manquera au second.
/// </remarks>
public interface ICertificateur
{
    bool EnCours(string piece);

    Task<IssueCertification> CertifierAsync(
        string piece, string modePaiement, short domaine, string origine,
        CancellationToken arret = default);
}

/// <summary>
/// Le chemin unique par lequel une certification demandée à la main passe.
/// </summary>
/// <remarks>
/// Deux écrans mènent ici — le tableau local et la demande venue du SaaS — et
/// c'est précisément pourquoi ce chemin est unique. Ce projet a corrigé sept
/// défauts de la même forme : une règle qui vit chez un appelant finit par
/// manquer au second. Le refus d'une facture d'essai, l'identité du dossier,
/// la liste des gabarits, le domaine des achats : à chaque fois, un second
/// appelant est arrivé et la règle n'était pas là.
///
/// Ce qui est tenu ici, et nulle part ailleurs :
/// <list type="bullet">
/// <item>la joignabilité s'éprouve AVANT d'entrer dans le chemin d'envoi ;</item>
/// <item>un verrou par pièce écarte deux envois simultanés ;</item>
/// <item>le mode de règlement est retenu AVANT que le lecteur ne construise le corps ;</item>
/// <item>rien ici ne contourne les contrôles métier ni le registre.</item>
/// </list>
/// </remarks>
public sealed class Certificateur(
    IServiceProvider fabrique,
    VerificateurStabilite stabilite,
    ISondeReseau sonde,
    ILogger<Certificateur> logger) : ICertificateur
{
    private readonly HashSet<string> _enCours = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _verrou = new();

    /// <summary>Vrai quand un envoi de cette pièce est déjà en vol.</summary>
    public bool EnCours(string piece)
    {
        lock (_verrou) return _enCours.Contains(piece);
    }

    public async Task<IssueCertification> CertifierAsync(
        string piece,
        string modePaiement,
        short domaine,
        string origine,
        CancellationToken arret = default)
    {
        lock (_verrou)
        {
            if (!_enCours.Add(piece))
            {
                return new IssueCertification(false,
                    $"Un envoi de la pièce {piece} est déjà en cours. Attendez sa réponse : " +
                    "deux envois feraient deux factures chez la DGI, et une facture certifiée " +
                    "ne s'annule pas.");
            }
        }

        try
        {
            // Une fois le POST parti, plus rien ne distingue une coupure
            // survenue avant de celle survenue après : la pièce reste en
            // Sending et ne repartira jamais toute seule. Mieux vaut ne pas
            // créer le doute.
            if (!await sonde.JoignableAsync(arret))
            {
                var essai = await sonde.EprouverAsync(arret);
                return new IssueCertification(false,
                    $"Rien n'a été envoyé : {essai.Explication} La pièce {piece} est intacte " +
                    "et reste certifiable dès que la plateforme répond.");
            }

            using var portee = fabrique.CreateScope();

            // Retenu AVANT l'envoi : c'est le lecteur qui relit la pièce et
            // construit le corps, et il ne prend en compte que ce qui est déjà
            // écrit. Le retenir après ferait partir la facture avec l'ancien
            // mode, ou celui du paramétrage — ce qui a longtemps été le cas.
            var compte = await CompteTiersAsync(portee, piece, domaine, arret);
            if (compte is not null)
            {
                await portee.ServiceProvider.GetRequiredService<IModesPaiementClients>()
                    .RetenirAsync(compte, modePaiement, arret);
            }

            logger.LogInformation(
                "{Origine} : certification de la pièce {Piece} ({Domaine}) demandée à la main, " +
                "mode de règlement « {Mode} » ({Code}).",
                origine, piece, SageDomaines.Libelle(domaine),
                ModePaiementFne.Libelle(modePaiement), modePaiement);

            var resultat = await portee.ServiceProvider.GetRequiredService<InvoiceSender>()
                .EnvoyerAsync(piece, confirme: true, arret, domaine);

            var identite = resultat.Conversion?.Header.Identite ?? "";

            if (resultat.Reussi)
            {
                stabilite.Oublier(identite == "" ? piece : identite);
                logger.LogInformation("Pièce {Piece} certifiée ({Origine}). {Message}",
                    piece, origine, resultat.Message);
            }
            else
            {
                logger.LogWarning("Pièce {Piece} non certifiée ({Origine}) : {Etat}. {Message}",
                    piece, origine, resultat.Etat, resultat.Message);
            }

            return new IssueCertification(
                resultat.Reussi,
                resultat.Message,
                resultat.Etat,
                resultat.Reponse?.ReferenceFne ?? "",
                resultat.Reponse?.CodeHttp,

                // Le corps brut, tel quel. Le reformuler reviendrait à
                // interpréter un message dont nous ne connaissons pas encore le
                // vocabulaire — et c'est ce vocabulaire qu'on cherche à
                // apprendre.
                resultat.Reponse?.CorpsBrut ?? "",
                identite);
        }
        finally
        {
            lock (_verrou) _enCours.Remove(piece);
        }
    }

    private static async Task<string?> CompteTiersAsync(
        IServiceScope portee, string piece, short domaine, CancellationToken arret)
    {
        var lecteur = portee.ServiceProvider.GetRequiredService<InvoiceBatchReader>();
        var lot = await lecteur.ReadAsync(
            InvoiceQuery.Piece(piece) with { Domaine = domaine }, arret);

        var tiers = lot.Conversions.FirstOrDefault()?.Header.Tiers;
        return string.IsNullOrWhiteSpace(tiers) ? null : tiers;
    }
}
