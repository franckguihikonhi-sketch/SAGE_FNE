using Microsoft.Extensions.Logging;
using SageFne.Agent.Certification;
using SageFne.Core.Fne;
using SageFne.Core.Models.Sage;
using SageFne.Core.Saas;

namespace SageFne.Agent.Saas;

/// <summary>
/// Ce que l'agent fait des clics venus de l'écran distant.
/// </summary>
/// <remarks>
/// Une demande dit « quelqu'un a cliqué », jamais « certifie ». L'agent la
/// relit, refait <b>tous</b> ses contrôles par le même chemin que le tableau
/// local, et décide. Le registre reste la seule autorité sur ce qui peut
/// partir : le cloud ne devient pas une seconde mémoire, parce que deux
/// mémoires qui se croient toutes deux vraies ont déjà certifié une facture
/// deux fois sur ce dossier.
///
/// L'ordre compte, comme partout ici : <b>la demande est prise avant d'agir</b>.
/// Si la machine s'arrête entre les deux, la demande reste « prise » et ne sera
/// jamais rejouée. Une demande bloquée se voit et se règle à la main ; une
/// demande rejouée fabrique un doublon.
/// </remarks>
public sealed class TraiteurDemandes(
    IDemandesClient demandes,
    ICertificateur certificateur,
    ILogger<TraiteurDemandes> logger)
{
    public async Task<int> TraiterAsync(int plafond, CancellationToken arret = default)
    {
        if (!demandes.Actif) return 0;

        var enAttente = await demandes.EnAttenteAsync(plafond, arret);
        if (enAttente.Count == 0) return 0;

        var traitees = 0;

        foreach (var demande in enAttente)
        {
            if (arret.IsCancellationRequested) break;

            // Réservée d'abord. C'est PostgreSQL qui départage : si une autre
            // instance l'a prise entre la lecture et ici, l'update ne touche
            // aucune ligne et nous passons.
            if (!await demandes.PrendreAsync(demande.Id, arret))
            {
                logger.LogInformation(
                    "Demande {Id} (pièce {Piece}) déjà prise ailleurs : rien n'est fait.",
                    demande.Id, demande.Piece);
                continue;
            }

            var mode = ModePaiementFne.Normaliser(demande.ModePaiement);

            if (mode is null)
            {
                // La base contraint déjà les six codes, mais un schéma plus
                // récent que l'agent pourrait en porter un de plus. Refuser
                // vaut mieux qu'envoyer un mode que la DGI ne connaît pas.
                await demandes.TrancherAsync(demande.Id, false,
                    $"Mode de règlement « {demande.ModePaiement} » inconnu de cet agent. " +
                    "Rien n'a été envoyé.", arret);
                continue;
            }

            var domaine = SageDomaines.DepuisIdentite(demande.Identite);

            var issue = await certificateur.CertifierAsync(
                demande.Piece, mode, domaine, "Demande SaaS", arret);

            await demandes.TrancherAsync(demande.Id, issue.Reussi, Verdict(issue), arret);
            traitees++;
        }

        return traitees;
    }

    /// <summary>
    /// Ce qui est réinscrit en base, pour que le refus se lise depuis l'écran.
    /// </summary>
    /// <remarks>
    /// La réponse de la plateforme est reprise mot pour mot quand il y en a
    /// une : « 400 Bad Request » seul ne dit pas ce qui cloche, et c'est le
    /// corps qui le dit. Le reformuler reviendrait à interpréter un vocabulaire
    /// que nous cherchons encore à apprendre.
    /// </remarks>
    private static string Verdict(IssueCertification issue) =>
        issue.ReponsePlateforme == ""
            ? issue.Message
            : $"{issue.Message} Réponse de la plateforme : {issue.ReponsePlateforme}";
}
