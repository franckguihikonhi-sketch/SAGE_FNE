using SageFne.Reader.Mapping;

namespace SageFne.Reader.Tests;

/// <summary>
/// Les quatre types de facturation, et ce qu'une faute de frappe coûterait.
/// </summary>
public class GabaritFneTests
{
    [Theory]
    [InlineData("B2B")]
    [InlineData("B2C")]
    [InlineData("B2F")]
    [InlineData("B2G")]
    [InlineData("b2b")]
    [InlineData("  B2C  ")]
    public void Les_quatre_types_du_portail_sont_reconnus(string gabarit) =>
        Assert.True(GabaritFne.Reconnu(gabarit));

    [Theory]
    [InlineData("BTB")]
    [InlineData("B2X")]
    [InlineData("Entreprise")]
    [InlineData("")]
    [InlineData(null)]
    public void Tout_le_reste_est_refuse(string? gabarit) =>
        Assert.False(GabaritFne.Reconnu(gabarit));

    [Fact]
    public void Seul_le_gabarit_entreprise_exige_le_ncc()
    {
        // Un consommateur final n'a pas de numéro contribuable à donner. Ce que
        // B2F et B2G exigent n'a pas été vérifié auprès de la DGI : tant que ce
        // n'est pas tranché, on n'exige rien plutôt que d'exiger à tort.
        Assert.True(GabaritFne.ExigeNcc("B2B"));
        Assert.False(GabaritFne.ExigeNcc("B2C"));
        Assert.False(GabaritFne.ExigeNcc("B2F"));
        Assert.False(GabaritFne.ExigeNcc("B2G"));
    }

    [Fact]
    public void Un_gabarit_inconnu_n_exige_rien_et_ne_certifie_rien()
    {
        // Il ne doit pas exiger le NCC — ce serait faire comme si on savait ce
        // qu'il veut dire. C'est la validation qui le refuse, en amont.
        Assert.False(GabaritFne.ExigeNcc("BTB"));
        Assert.Equal("inconnu", GabaritFne.Libelle("BTB"));
    }

    [Fact]
    public void Le_message_d_erreur_nomme_les_quatre()
    {
        foreach (var attendu in new[] { "B2B", "B2C", "B2F", "B2G" })
        {
            Assert.Contains(attendu, GabaritFne.Attendus);
        }
    }
}
