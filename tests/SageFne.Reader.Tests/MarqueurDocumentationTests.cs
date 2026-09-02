using SageFne.Reader.Batch;

namespace SageFne.Reader.Tests;

/// <summary>
/// Un mot que la documentation donne à remplacer n'est pas une valeur.
/// </summary>
/// <remarks>
/// Trois fois de suite, un marqueur écrit pour être remplacé a été collé tel
/// quel : « &lt;numéro&gt; », « …ce que la commande ci-dessus a montré… », puis
/// « LA_REFERENCE ». La troisième a inscrit au registre une référence FNE
/// inexistante sur la pièce 1222 — exactement ce que ce projet s'interdit.
///
/// La faute n'est pas celle de qui l'a tapée. Un outil qui accepte comme valeur
/// un mot que sa propre aide donne comme à remplacer est un outil mal fait.
/// </remarks>
public class MarqueurDocumentationTests
{
    [Theory]
    [InlineData("LA_REFERENCE")]
    [InlineData("la_reference")]
    [InlineData("REF")]
    [InlineData("TA_REFERENCE_FNE")]
    [InlineData("A_COMPLETER")]
    [InlineData("<la référence du portail>")]
    [InlineData("…ce que le portail a montré…")]
    [InlineData("« votre référence »")]
    public void Un_marqueur_ne_peut_pas_devenir_une_reference(string marqueur)
    {
        var ligne = CommandLine.Parse(["debloquer", "1222", "--reference", marqueur, "--confirmer"]);

        Assert.NotEmpty(ligne.Erreurs);
        Assert.Contains(ligne.Erreurs, e => e.Contains(marqueur.Trim()));
    }

    [Theory]
    [InlineData("9606133220250903143028519")]
    [InlineData("FNE-2026-000123")]
    [InlineData("478925k")]
    public void Une_vraie_reference_passe(string reference)
    {
        var ligne = CommandLine.Parse(["debloquer", "1222", "--reference", reference, "--confirmer"]);

        Assert.Empty(ligne.Erreurs);
        Assert.Equal(reference, ligne.Reference);
    }

    [Fact]
    public void Le_mot_fautif_reste_nommable_pour_le_corriger()
    {
        // --reference-actuelle sert justement à désigner l'écriture fautive.
        // La filtrer rendrait la faute inréparable par l'outil qui l'a permise.
        var ligne = CommandLine.Parse([
            "corriger-reconciliation", "1222", "--supprimer-reference",
            "--reference-actuelle", "LA_REFERENCE",
            "--motif", "Marqueur de documentation inscrit par erreur", "--confirmer",
        ]);

        Assert.Empty(ligne.Erreurs);
        Assert.Equal("LA_REFERENCE", ligne.ReferenceActuelle);
    }

    [Fact]
    public void Un_motif_qui_est_reste_a_l_etat_de_trou_est_refuse()
    {
        // Le motif est la trace écrite d'une décision humaine. « … » n'en est
        // pas une, et il resterait au registre pour toujours.
        var ligne = CommandLine.Parse([
            "corriger-reconciliation", "1222", "--supprimer-reference",
            "--reference-actuelle", "LA_REFERENCE", "--motif", "…", "--confirmer",
        ]);

        Assert.NotEmpty(ligne.Erreurs);
    }
}
