using SageFne.Core.Data;
using SageFne.Core.Models.Sage;

namespace SageFne.Core.Tests;

/// <summary>
/// Voir ce que le dossier contient hors des ventes.
/// </summary>
/// <remarks>
/// Le middleware ne lit que <c>DO_Domaine = 0</c>, à quatre endroits. Le reste
/// du dossier — achats, stocks, ce que celui-ci utilise — n'avait jamais été
/// regardé, et il a fallu s'en apercevoir le jour où la question des achats
/// s'est posée. Cette commande est le seul endroit d'où on peut le voir.
/// </remarks>
public class DomainesTests
{
    [Fact]
    public async Task L_inventaire_compte_par_domaine_et_par_type()
    {
        var depot = new DemoSageInvoiceRepository();
        var domaines = await depot.GetDomainesAsync();

        Assert.NotEmpty(domaines);

        // Les deux domaines, désormais : le jeu d'essai porte des ventes et des
        // achats, si bien que le chemin d'achat est exercé et non supposé.
        Assert.Contains(domaines, d => d.Domaine == SageDomaines.Vente);
        Assert.Contains(domaines, d => d.Domaine == SageDomaines.Achat);

        // Les deux états d'une facture, comptés séparément parce qu'ils le sont
        // dans la table — c'est justement ce que l'inventaire doit montrer.
        Assert.Contains(domaines, d =>
            d.Domaine == SageDomaines.Vente && d.Type == SageDocumentTypes.Facture);
        Assert.Contains(domaines, d =>
            d.Domaine == SageDomaines.Vente && d.Type == SageDocumentTypes.FactureComptabilisee);
        Assert.Contains(domaines, d =>
            d.Domaine == SageDomaines.Achat && d.Type == SagePurchaseTypes.Facture);
    }

    [Fact]
    public async Task Chaque_couple_porte_un_exemplaire_a_reconnaitre()
    {
        // Un compte sans exemplaire ne se reconnaît pas. « 412 documents de
        // type 13 » ne dit rien à personne ; une pièce et un compte tiers, si.
        var domaines = await new DemoSageInvoiceRepository().GetDomainesAsync();

        Assert.All(domaines, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Exemple));
            Assert.False(string.IsNullOrWhiteSpace(d.Tiers));
            Assert.True(d.Nombre > 0);
        });
    }

    [Fact]
    public async Task L_inventaire_est_ordonne_par_domaine_puis_par_type()
    {
        var domaines = await new DemoSageInvoiceRepository().GetDomainesAsync();

        var attendu = domaines
            .OrderBy(d => d.Domaine).ThenBy(d => d.Type)
            .Select(d => (d.Domaine, d.Type))
            .ToList();

        Assert.Equal(attendu, domaines.Select(d => (d.Domaine, d.Type)).ToList());
    }

    [Fact]
    public void La_requete_ne_filtre_aucun_domaine()
    {
        // Le point entier de cette commande. Un filtre sur DO_Domaine la
        // rendrait aveugle à ce qu'elle existe pour montrer — et le défaut
        // serait invisible sur le jeu d'essai, qui n'a que des ventes.
        Assert.DoesNotContain("DO_Domaine = @domaine", SageInvoiceRepository.SqlDomaines);
        Assert.Contains("group by e.DO_Domaine, e.DO_Type", SageInvoiceRepository.SqlDomaines);
    }

    [Fact]
    public void La_requete_passe_le_garde_fou_de_lecture_seule()
    {
        // Elle ne l'a pas passé d'emblée : écrite avec une CTE, elle commençait
        // par « with », que le garde-fou refuse — il exige « select ». Elle
        // aurait échoué à l'exécution, sur le poste, pas ici.
        //
        // Le garde-fou n'a pas été assoupli pour autant : c'est la requête qui
        // a été réécrite. Affaiblir la barrière pour faire passer sa propre
        // requête, c'est prendre le problème par le mauvais bout.
        ReadOnlyGuard.Verify(SageInvoiceRepository.SqlDomaines);
    }

    [Fact]
    public void L_exemplaire_vient_d_une_seule_ligne()
    {
        // Deux max() séparés auraient pu prendre la pièce d'un document et le
        // compte d'un autre : un exemplaire qui n'existe pas, présenté comme
        // s'il existait. Concaténés, ils viennent forcément de la même ligne.
        Assert.Contains("max(rtrim(e.DO_Piece)", SageInvoiceRepository.SqlDomaines);
        Assert.DoesNotContain("max(e.DO_Tiers)", SageInvoiceRepository.SqlDomaines);
    }
}
