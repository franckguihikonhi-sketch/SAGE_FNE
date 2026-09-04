using Microsoft.Extensions.Logging.Abstractions;
using SageFne.Agent.Surveillance;
using SageFne.Core.Certification;
using SageFne.Core.Fne;

namespace SageFne.Agent.Tests;

/// <summary>
/// L'anti-doublon survit au redémarrage du service.
/// </summary>
/// <remarks>
/// Deux mémoires, et il ne faut jamais les confondre.
///
/// Le suivi de stabilité vit en RAM : le perdre ne fait que retarder un envoi,
/// puisqu'une pièce redevenue « jamais vue » attend un tour de plus.
///
/// Le registre des certifications vit sur disque, et c'est lui seul qui empêche
/// le doublon. S'il dépendait de la mémoire du service, un redémarrage après un
/// envoi resté sans réponse rendrait la facture renvoyable — et la DGI l'aurait
/// deux fois.
/// </remarks>
public class AntiDoublonTests : IDisposable
{
    private readonly string _dossier =
        Path.Combine(Path.GetTempPath(), $"registre-agent-{Guid.NewGuid():N}");

    public AntiDoublonTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        if (Directory.Exists(_dossier)) Directory.Delete(_dossier, recursive: true);
    }

    private string Fichier => Path.Combine(_dossier, "certifications.json");

    private JsonCertificationLedger Registre() =>
        new(Fichier, NullLogger<JsonCertificationLedger>.Instance);

    private static CertifiedInvoice Trace(EtatFne etat) => new()
    {
        Identite = "0/6/1221",
        Piece = "1221",
        Etat = etat,
        Empreinte = "abc",
    };

    [Fact]
    public async Task Un_envoi_sans_reponse_reste_connu_apres_redemarrage()
    {
        // Le cas qui compte : POST parti, 500 reçu, service redémarré. Sans
        // registre persistant, la pièce repartirait — et c'est très exactement
        // ce qui a produit le doublon de la 1072.
        await Registre().RecordAsync(Trace(EtatFne.Sending));

        // Un registre neuf : c'est ce que voit le service au redémarrage.
        var apresRedemarrage = await Registre().LookupAsync(["0/6/1221"]);

        Assert.True(apresRedemarrage.ContainsKey("0/6/1221"));
        Assert.Equal(EtatFne.Sending, apresRedemarrage["0/6/1221"].Etat);
    }

    [Fact]
    public async Task Une_piece_certifiee_reste_certifiee_apres_redemarrage()
    {
        await Registre().RecordAsync(Trace(EtatFne.Certified) with { ReferenceFne = "FNE-1" });

        var apresRedemarrage = await Registre().LookupAsync(["0/6/1221"]);

        Assert.Equal(EtatFne.Certified, apresRedemarrage["0/6/1221"].Etat);
        Assert.Equal("FNE-1", apresRedemarrage["0/6/1221"].ReferenceFne);
    }

    [Fact]
    public async Task Une_piece_deposee_au_portail_reste_deposee_apres_redemarrage()
    {
        await Registre().RecordAsync(Trace(EtatFne.Transmise));

        var apresRedemarrage = await Registre().LookupAsync(["0/6/1221"]);

        Assert.Equal(EtatFne.Transmise, apresRedemarrage["0/6/1221"].Etat);
    }

    [Fact]
    public async Task Le_journal_des_tentatives_survit_aussi()
    {
        // Compter les envois partis est ce qui alerte au second. Sur la 1072,
        // la trace était reconstruite à neuf à chaque envoi : le registre
        // affirmait « cette pièce n'est jamais partie » alors qu'elle l'était.
        await Registre().RecordAsync(
            Trace(EtatFne.Sending).AvecTentative(GenreTentative.Envoi, "POST n° 1"));

        var apresRedemarrage = await Registre().LookupAsync(["0/6/1221"]);

        Assert.Equal(1, apresRedemarrage["0/6/1221"].NombreEnvois);
    }

    [Fact]
    public void La_stabilite_ne_sert_jamais_d_anti_doublon()
    {
        // Le test qui garde la frontière. Si quelqu'un faisait un jour reposer
        // l'anti-doublon sur ce suivi, un redémarrage rouvrirait tous les
        // envois — et rien ne le signalerait avant le portail de la DGI.
        var verificateur = new VerificateurStabilite(TimeSpan.Zero);
        verificateur.Constater("0/6/1221", "abc");

        var apresRedemarrage = new VerificateurStabilite(TimeSpan.Zero);

        Assert.Equal(1, verificateur.EnObservation);
        Assert.Equal(0, apresRedemarrage.EnObservation);
        Assert.Null(apresRedemarrage.Derniere("0/6/1221"));
    }
}
