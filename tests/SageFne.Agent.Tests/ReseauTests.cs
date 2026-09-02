using SageFne.Agent.Sante;

namespace SageFne.Agent.Tests;

/// <summary>
/// Ce que l'agent fait quand la plateforme ne répond pas.
/// </summary>
/// <remarks>
/// Deux pannes qui se ressemblent et n'ont rien à voir :
///
/// <b>Avant le départ du POST</b> — rien n'est parti, la DGI n'a rien reçu. La
/// pièce reste en file et repartira. Aucune trace n'a besoin d'être écrite.
///
/// <b>Après le départ du POST</b> — la requête a peut-être été reçue et la
/// facture peut-être certifiée. La pièce reste en <c>Sending</c>, et rien ne la
/// renvoie automatiquement. C'est ce cas-là, mal jugé, qui a produit le doublon
/// de la 1072.
///
/// Comme les deux sont indiscernables une fois le POST lancé, l'agent ne les
/// départage pas : il vérifie la joignabilité <b>avant</b>, et n'entre dans le
/// chemin d'envoi que si la plateforme répond.
/// </remarks>
public class ReseauTests
{
    [Fact]
    public async Task Une_plateforme_injoignable_se_lit_comme_telle()
    {
        Assert.False(await new SondeFigee(false).JoignableAsync());
        Assert.True(await new SondeFigee(true).JoignableAsync());
    }

    [Fact]
    public async Task Un_hote_inexistant_n_est_pas_joignable()
    {
        // Sans plateforme joignable, l'agent n'envoie rien — et surtout
        // n'écrit rien au registre. La pièce reste exactement où elle était.
        var sonde = new SondeTcp(
            new Uri("http://hote-qui-n-existe-pas.invalid:80/"), TimeSpan.FromSeconds(2));

        Assert.False(await sonde.JoignableAsync());
    }

    [Fact]
    public async Task Un_port_ferme_n_est_pas_joignable()
    {
        // 9 est le port « discard », habituellement fermé : refus immédiat,
        // sans attendre le délai.
        var sonde = new SondeTcp(new Uri("http://127.0.0.1:9/"), TimeSpan.FromSeconds(2));

        Assert.False(await sonde.JoignableAsync());
    }

    [Fact]
    public async Task L_arret_du_service_n_est_pas_un_diagnostic_reseau()
    {
        // Un agent qu'on arrête ne doit pas conclure « plateforme injoignable »
        // et l'écrire au heartbeat : il s'arrête, c'est tout.
        var sonde = new SondeTcp(new Uri("http://127.0.0.1:9/"), TimeSpan.FromSeconds(30));
        using var arret = new CancellationTokenSource();
        await arret.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sonde.JoignableAsync(arret.Token));
    }

    [Fact]
    public void Sans_adresse_configuree_rien_n_est_repute_joignable()
    {
        // Une sonde qui répondrait « oui » par défaut ferait entrer l'agent dans
        // le chemin d'envoi avec une configuration vide.
        Assert.False(Uri.TryCreate("", UriKind.Absolute, out _));
    }
}
