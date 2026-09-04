using SageFne.Core.Batch;
using SageFne.Core.Data;
using SageFne.Core.Fne;
using SageFne.Core.Validation;

namespace SageFne.Agent.Surveillance;

/// <summary>
/// Décide, pièce par pièce, si elle peut partir — et ne décide que cela.
/// </summary>
/// <remarks>
/// <b>Aucune règle fiscale ne vit ici.</b> Les contrôles métier, le mapping des
/// taxes, la TVA à 0 %, le NCC, l'anti-doublon, l'empreinte : tout vient de
/// <see cref="InvoiceBatchReader"/>, exactement comme pour le CLI. Le jour où
/// une règle change, elle change à un seul endroit et les deux la suivent.
///
/// Ce que le moteur ajoute, et qui n'existe pas en ligne de commande : la
/// stabilité. Un humain qui tape « envoyer 1221 » sait que sa saisie est finie.
/// Un agent qui lit toutes les minutes ne le sait pas.
/// </remarks>
public sealed class MoteurSurveillance(
    InvoiceBatchReader lecteur,
    VerificateurStabilite stabilite,
    ModeAgent mode,
    SuiviRefus? refus = null)
{
    private readonly SuiviRefus _refus = refus ?? new SuiviRefus();

    /// <summary>
    /// Examine les pièces d'une requête et dit ce qu'il advient de chacune.
    /// </summary>
    /// <remarks>
    /// Ne contacte aucune API et n'écrit nulle part : cette méthode décide,
    /// elle n'agit pas. C'est ce qui la rend vérifiable sans plateforme.
    /// </remarks>
    public async Task<IReadOnlyList<DecisionAgent>> ExaminerAsync(
        InvoiceQuery requete, CancellationToken cancellation = default)
    {
        var lot = await lecteur.ReadAsync(requete, cancellation);
        var decisions = new List<DecisionAgent>(lot.Conversions.Count);

        foreach (var conversion in lot.Conversions)
        {
            decisions.Add(Decider(conversion));
        }

        return decisions;
    }

    /// <summary>Le sort d'une pièce, dans l'ordre où les questions se posent.</summary>
    public DecisionAgent Decider(InvoiceConversion conversion)
    {
        var identite = conversion.Header.Identite;
        var piece = conversion.Header.Piece;

        DecisionAgent Retenir(MotifAttente motif, string explication) =>
            new(piece, identite, motif, explication);

        // 1. Ce que le registre a déjà tranché passe avant tout le reste. Une
        //    pièce certifiée, déposée au portail ou partie sans réponse ne
        //    repart pas — et c'est le lecteur, pas l'agent, qui l'établit.
        //
        //    Le passage DO_Type 6 → 7 est couvert ici sans code particulier :
        //    l'identité ne change pas à la comptabilisation, donc le registre
        //    reconnaît la même pièce et l'écarte.
        if (conversion.Etat is EtatPiece.DejaCertifiee or EtatPiece.Transmise or EtatPiece.EnSuspens)
        {
            stabilite.Oublier(identite);
            _refus.Oublier(identite);
            return Retenir(MotifAttente.DejaTraitee,
                $"Pièce {piece} : {conversion.LibelleEtat}. Rien ne repart automatiquement.");
        }

        // 2. Certifiée puis modifiée dans Sage. Le renvoi ne corrige rien : il
        //    ferait deux factures là où la loi en veut une et un avoir.
        if (conversion.Etat == EtatPiece.ModifieeDepuis)
        {
            stabilite.Oublier(identite);
            return Retenir(MotifAttente.DejaTraitee,
                $"DOCUMENT_MODIFIE_APRES_CERTIFICATION — pièce {piece} certifiée puis modifiée " +
                "dans Sage. Aucun second envoi : la correction passe par un avoir.");
        }

        // 3. Un refus déjà essuyé sur ce contenu exact. Le lecteur laisse la
        //    pièce repartir après un échec — c'est juste pour un humain qui
        //    décide de réessayer. Un agent qui relit toutes les minutes
        //    rejouerait le même envoi indéfiniment.
        if (conversion.Certification is { } trace
            && trace.Etat == EtatFne.Error
            && conversion.Empreinte != ""
            && string.Equals(trace.Empreinte, conversion.Empreinte, StringComparison.Ordinal))
        {
            var suite = _refus.Constater(identite, conversion.Empreinte, trace.CertifieeLe);

            if (!suite.PeutRepartir)
            {
                stabilite.Oublier(identite);
                var motifPlateforme = trace.Erreur == "" ? "" : $" — {trace.Erreur}";

                return Retenir(MotifAttente.RefusInchange, suite.Reste is { } reste
                    ? $"Pièce {piece} : refusée{motifPlateforme}. " +
                      $"{suite.Tentatives} refus sur ce contenu ; prochain essai dans " +
                      $"{reste.TotalMinutes:0} min. Corrigez-la dans Sage et elle repartira aussitôt."
                    : $"Pièce {piece} : refusée{motifPlateforme}. " +
                      $"{suite.Tentatives} refus sur ce contenu exact — l'agent cesse de " +
                      "réessayer. Le refus n'est pas passager : corrigez la pièce dans Sage, " +
                      "ou envoyez-la à la main pour lire la réponse de la plateforme.");
            }
        }

        // 4. Le périmètre. Avant les contrôles métier : une facture de 2024
        //    n'a pas à être annoncée « bloquée » parce qu'il lui manque un NCC.
        //    On ne l'envoie pas, et ce n'est pas un défaut à corriger.
        if (conversion.Etat == EtatPiece.HorsPerimetre)
        {
            stabilite.Oublier(identite);
            return Retenir(MotifAttente.HorsPerimetre,
                $"Pièce {piece} : {conversion.LibelleEtat}.");
        }

        // 5. Les contrôles métier. Une pièce non conforme est bloquée, et le
        //    reste tant que Sage n'a pas changé.
        if (conversion.Etat != EtatPiece.ACertifier)
        {
            var causes = conversion.Report.Constats
                .Where(constat => constat.Severite == Severite.Erreur)
                .Select(constat => constat.Code)
                .Distinct()
                .ToList();

            return Retenir(MotifAttente.NonConforme,
                $"Pièce {piece} bloquée : {(causes.Count == 0 ? conversion.LibelleEtat : string.Join(", ", causes))}.");
        }

        // 6. La stabilité. Elle vient après les contrôles : inutile d'observer
        //    deux fois une pièce que rien ne laissera partir.
        var attente = stabilite.Constater(identite, conversion.Empreinte);
        if (attente != MotifAttente.Aucun)
        {
            return Retenir(attente, attente switch
            {
                MotifAttente.JamaisVue =>
                    $"Pièce {piece} vue pour la première fois : son contenu sera revérifié " +
                    "avant tout envoi.",
                MotifAttente.ContenuInstable =>
                    $"Pièce {piece} : le contenu a changé depuis la dernière lecture. " +
                    "La saisie n'est pas finie.",
                MotifAttente.DelaiNonEcoule =>
                    // Le compte à rebours, en toutes lettres. « Le délai n'est
                    // pas écoulé » ne dit ni combien il reste ni quel délai
                    // s'applique : on ne peut pas distinguer une attente
                    // normale d'un réglage qui n'est jamais arrivé jusqu'au
                    // service.
                    $"Pièce {piece} : contenu identique, il reste " +
                    $"{stabilite.Reste(identite)?.TotalSeconds ?? 0:0} s sur les " +
                    $"{stabilite.Delai.TotalMinutes:0.#} min de stabilité.",
                _ => $"Pièce {piece} : non traduisible, rien à observer.",
            });
        }

        // 7. Enfin seulement, le mode. Il ne décide pas de la conformité — il
        //    décide de qui appuie sur le bouton.
        if (mode != ModeAgent.Automatic)
        {
            return Retenir(MotifAttente.ModeNonAutomatique,
                $"Pièce {piece} conforme et stable. Mode « {mode} » : l'envoi attend une " +
                "décision humaine.");
        }

        return Retenir(MotifAttente.Aucun,
            $"Pièce {piece} conforme et stable : envoyable.");
    }
}
