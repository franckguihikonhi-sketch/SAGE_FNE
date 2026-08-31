namespace SageFne.Reader.Certification;

/// <param name="Proposee">Ce que les preuves internes désignent.</param>
/// <param name="Justification">Sur quoi elles reposent, mot pour mot.</param>
/// <param name="Concluante">Faux quand rien ne permet de trancher.</param>
public sealed record DiagnosticSource(
    SourceCertification Proposee,
    string Justification,
    bool Concluante);

/// <summary>
/// Retrouver d'où vient une entrée que le registre ne qualifie pas.
/// </summary>
/// <remarks>
/// Les entrées écrites avant l'ajout du champ <c>source</c> n'en portent aucun.
/// Rien ne les distingue d'un simple oubli, sinon ce que la commande qui les a
/// créées y a laissé écrit : la réconciliation manuelle appose une attestation
/// reconnaissable.
///
/// Cette lecture ne sert qu'à réparer, jamais à décider en routine. Elle
/// n'affirme qu'une chose et une seule — la réconciliation manuelle — parce
/// qu'elle seule laisse une trace textuelle sans ambiguïté. Conclure « réponse
/// de la plateforme » depuis l'absence de preuve serait refaire l'erreur qui a
/// rendu ce diagnostic nécessaire.
/// </remarks>
public static class SourceHeuristique
{
    /// <summary>
    /// Ce que la réconciliation manuelle écrit, et que rien d'autre n'écrit.
    /// </summary>
    /// <remarks>
    /// Exigées ensemble : « réconciliation manuelle » seul pourrait figurer
    /// dans un motif saisi à la main, et « portail DGI » dans un constat de
    /// déblocage. Les trois réunies ne se produisent que sous la plume de
    /// <c>ReconcilierAsync</c>.
    /// </remarks>
    private static readonly string[] Marques =
    [
        "réconciliation manuelle",
        "constatée sur le portail dgi par l'exploitant",
        "non observée par le middleware",
    ];

    public static DiagnosticSource Diagnostiquer(CertifiedInvoice entree)
    {
        if (entree.Source != SourceCertification.Inconnue)
        {
            return new DiagnosticSource(
                entree.Source,
                $"L'entrée porte déjà une source explicite : {entree.Source}. " +
                "Rien n'est déduit d'une ligne qui se déclare elle-même.",
                Concluante: false);
        }

        if (entree.Etat != Fne.EtatFne.Certified)
        {
            return new DiagnosticSource(
                SourceCertification.Inconnue,
                $"L'entrée est en « {entree.Etat} », pas en « Certified » : seules les " +
                "certifications ont une origine à établir.",
                Concluante: false);
        }

        // Les deux zones de texte : l'attestation partait dans « erreur » avant
        // que « motif » n'existe, et les registres d'alors sont précisément ceux
        // qu'il faut requalifier.
        var texte = $"{entree.Erreur}\n{entree.Motif}".ToLowerInvariant();
        var trouvees = Marques.Where(texte.Contains).ToList();

        if (trouvees.Count != Marques.Length)
        {
            return new DiagnosticSource(
                SourceCertification.Inconnue,
                trouvees.Count == 0
                    ? "Aucune trace d'une réconciliation manuelle dans cette entrée. Son " +
                      "origine reste inconnue : mieux vaut l'ignorer que la deviner."
                    : $"Traces incomplètes ({trouvees.Count} sur {Marques.Length}) : " +
                      "une requalification demande l'attestation entière, pas un fragment.",
                Concluante: false);
        }

        return new DiagnosticSource(
            SourceCertification.ReconciliationManuelle,
            "L'entrée porte l'attestation complète que seule « reconcilier » écrit : " +
            "« réconciliation manuelle », « constatée sur le portail DGI par l'exploitant », " +
            "« non observée par le middleware ». Une réponse de la plateforme ne contient " +
            "aucune de ces mentions.",
            Concluante: true);
    }
}
