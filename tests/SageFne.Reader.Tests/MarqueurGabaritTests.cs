using SageFne.Core.Validation;

namespace SageFne.Core.Tests;

/// <summary>
/// Reconnaître un texte de documentation là où on attend une valeur.
/// </summary>
/// <remarks>
/// Quatre fois un exemple a été recopié tel quel dans une commande. La
/// quatrième, « VOTRE_ETAB » s'est installé dans l'identité du dossier auprès
/// de la DGI : la plateforme refusait toutes les factures, et plus rien
/// n'avertissait — la valeur n'était reconnue comme un trou par aucune des deux
/// listes, qui vivaient en double et avaient divergé.
/// </remarks>
public class MarqueurGabaritTests
{
    [Theory]
    [InlineData("VOTRE_POINT")]
    [InlineData("VOTRE_ETAB")]
    [InlineData("VOTRE_REFERENCE")]
    [InlineData("votre_etablissement")]
    [InlineData("MON_NCC")]
    [InlineData("YOUR_KEY")]
    [InlineData("A_COMPLETER")]
    [InlineData("A_RENSEIGNER")]
    [InlineData("TODO")]
    [InlineData("XXX")]
    [InlineData("LA_REFERENCE")]
    [InlineData("CHANGEME")]
    [InlineData("<votre établissement>")]
    [InlineData("« à remplir »")]
    [InlineData("la référence…")]
    [InlineData("  VOTRE_POINT  ")]
    public void Un_gabarit_est_reconnu(string valeur) =>
        Assert.True(MarqueurGabarit.Est(valeur), $"« {valeur} » aurait dû être vu comme un gabarit.");

    [Theory]
    [InlineData("FISH-AFRIC")]
    [InlineData("2304903U26000000002")]
    [InlineData("1432262S")]
    [InlineData("ABIDJAN-01")]
    [InlineData("B2B")]
    [InlineData("deferred")]

    // Un vrai code peut contenir le mot, sans être le mot. Le préfixe et
    // l'égalité stricte s'en chargent : ni « REFERENCE-2026 » ni
    // « MARCHE_XXX_2026 » ne sont des trous à remplir.
    [InlineData("REFERENCE-2026")]
    [InlineData("MARCHE_XXX_2026")]
    public void Une_vraie_valeur_passe(string valeur) =>
        Assert.False(MarqueurGabarit.Est(valeur), $"« {valeur} » est une valeur, pas un gabarit.");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Le_vide_n_est_pas_un_gabarit_mais_reste_absent(string? valeur)
    {
        // Deux questions distinctes, et les confondre reviendrait à dire d'un
        // champ vide qu'il porte un texte à remplacer — ce qui n'aiderait pas
        // celui qui cherche pourquoi.
        Assert.False(MarqueurGabarit.Est(valeur));
        Assert.True(MarqueurGabarit.Absent(valeur));
    }

    [Fact]
    public void Une_identite_en_gabarit_bloque_l_envoi()
    {
        // Le lien avec ce qui compte : c'est ce contrôle que l'expéditeur
        // interroge avant tout appel.
        var manques = FneCompleteness.IdentiteAControler(new SageFne.Core.Models.Fne.FneInvoice
        {
            PointOfSale = "VOTRE_POINT",
            Establishment = "VOTRE_ETAB",
        });

        Assert.Equal(2, manques.Count);
    }
}
