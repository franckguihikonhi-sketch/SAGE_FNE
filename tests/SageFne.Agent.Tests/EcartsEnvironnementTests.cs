using SageFne.Agent.Configuration;

namespace SageFne.Agent.Tests;

/// <summary>
/// Un réglage posé que le service n'a jamais reçu.
/// </summary>
/// <remarks>
/// Le gestionnaire de services de Windows garde en cache l'environnement
/// machine tel qu'il était à l'amorçage. Une variable posée cinq minutes plus
/// tôt peut donc rester invisible au service : il tourne parfaitement, sur
/// d'autres réglages que ceux qu'on croit avoir posés.
///
/// Sur ce poste, le délai de stabilité a été réglé à 2 minutes et rien ne
/// disait si le service en appliquait 2 ou 5. On attend alors sans comprendre,
/// et l'on conclut que l'automatisme ne fonctionne pas.
/// </remarks>
public class EcartsEnvironnementTests
{
    private static IReadOnlyList<string> Detecter(
        (string Variable, string Applique)[] applique,
        params (string Variable, string Machine)[] machine)
    {
        var surLaMachine = machine.ToDictionary(m => m.Variable, m => m.Machine);
        return EcartsEnvironnement.Detecter(
            applique.ToDictionary(a => a.Variable, a => a.Applique),
            nom => surLaMachine.TryGetValue(nom, out var valeur) ? valeur : null);
    }

    [Fact]
    public void Un_reglage_pose_mais_non_applique_est_signale()
    {
        var ecarts = Detecter(
            [("Agent__StabiliteMinutes", "5")],
            ("Agent__StabiliteMinutes", "2"));

        var ecart = Assert.Single(ecarts);
        Assert.Contains("« 2 » sur la machine", ecart);
        Assert.Contains("applique « 5 »", ecart);
        Assert.Contains("Redémarrez le poste", ecart);
    }

    [Fact]
    public void Un_reglage_qui_concorde_ne_dit_rien()
    {
        Assert.Empty(Detecter(
            [("Agent__StabiliteMinutes", "2")],
            ("Agent__StabiliteMinutes", "2")));
    }

    [Fact]
    public void Une_variable_absente_de_la_machine_n_est_pas_un_ecart()
    {
        // Le service tourne alors sur appsettings.json ou sur son défaut, ce
        // qui est parfaitement légitime. Le signaler serait du bruit sur chaque
        // démarrage.
        Assert.Empty(Detecter([("Agent__Mode", "Manual")]));
    }

    [Fact]
    public void Une_variable_vide_ne_compte_pas_pour_un_desaccord()
    {
        Assert.Empty(Detecter([("Agent__Mode", "Manual")], ("Agent__Mode", "   ")));
    }

    [Fact]
    public void La_casse_et_les_espaces_ne_font_pas_un_faux_desaccord()
    {
        Assert.Empty(Detecter(
            [("Agent__Mode", "Automatic")],
            ("Agent__Mode", "  automatic  ")));
    }

    [Fact]
    public void Plusieurs_reglages_non_appliques_sont_tous_dits()
    {
        var ecarts = Detecter(
            [("Agent__Mode", "Manual"), ("Agent__StabiliteMinutes", "5")],
            ("Agent__Mode", "Automatic"), ("Agent__StabiliteMinutes", "2"));

        Assert.Equal(2, ecarts.Count);
    }
}
