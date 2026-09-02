using SageFne.Core.Mapping;
using SageFne.Core.Models.Sage;

namespace SageFne.Core.Tests;

/// <summary>
/// Relevé sur le dossier HT : les trois fiches de F_TAXE portent toutes
/// TA_EdiCode = « VAT », <b>AIRSI compris</b>. Se fier à ce champ ferait
/// certifier l'AIRSI comme de la TVA. TA_Regroup, lui, sépare correctement.
/// </summary>
public class TaxCatalogueTests
{
    /// <summary>Les trois fiches réelles du dossier.</summary>
    private static readonly SageTaxDefinition[] Fiches =
    [
        new() { Code = "AIRSI", Intitule = "AIRSI", Taux = 1.5m, Regroupement = "AIRSI", EdiCode = "VAT" },
        new() { Code = "TVA", Intitule = "TVA/VENTE", Taux = 9m, Regroupement = "TVA", EdiCode = "VAT" },
        new() { Code = "TVA0", Intitule = "TVA/ACHAT", Taux = 18m, Regroupement = "TVA", EdiCode = "VAT" },
    ];

    private static TaxCatalogue Catalogue(params (string Code, string Nom)[] mappes) =>
        new(Fiches, mappes.ToDictionary(entree => entree.Code, entree => entree.Nom, StringComparer.OrdinalIgnoreCase));

    private static SageDocumentLine Ligne(
        decimal taxe1 = 0m, string code1 = "",
        decimal taxe2 = 0m, string code2 = "") => new()
    {
        Domaine = 0, Type = 6, Piece = "1219", Ligne = 1,
        ArticleReference = "13415001", Designation = "Queue De Boeuf",
        Quantite = 1m, PrixUnitaire = 2500m,
        Taxe1 = taxe1, CodeTaxe1 = code1,
        Taxe2 = taxe2, CodeTaxe2 = code2,
    };

    [Fact]
    public void TA_Regroup_separe_ce_que_TA_EdiCode_confond()
    {
        var catalogue = Catalogue();

        // Les trois fiches disent « VAT » en EDI. Le regroupement, non.
        Assert.Equal("AIRSI", catalogue.Groupe("AIRSI"));
        Assert.Equal("TVA", catalogue.Groupe("TVA"));
        Assert.Equal("TVA", catalogue.Groupe("TVA0"));
    }

    [Fact]
    public void L_AIRSI_mappe_part_bien_en_customTaxes()
    {
        var resultat = TaxMapping.Read(
            Ligne(taxe2: 1.5m, code2: "AIRSI"),
            CodeTvaZero.Tvad,
            Catalogue(("AIRSI", "AIRSI")));

        var prelevement = Assert.Single(resultat.CustomTaxes);
        Assert.Equal("AIRSI", prelevement.Name);
        Assert.Equal(1.5m, prelevement.Amount);
        Assert.Empty(resultat.PrelevementsSansMapping);
    }

    [Fact]
    public void Le_nom_FNE_vient_du_mapping_pas_du_code_Sage()
    {
        // Un dossier peut nommer sa taxe autrement que la DGI.
        var resultat = TaxMapping.Read(
            Ligne(taxe2: 1.5m, code2: "AIRSI"),
            CodeTvaZero.Tvad,
            Catalogue(("AIRSI", "AIRSI-CI")));

        Assert.Equal("AIRSI-CI", Assert.Single(resultat.CustomTaxes).Name);
    }

    [Fact]
    public void Un_prelevement_non_mappe_ne_part_pas_tout_seul()
    {
        // « AIB », du même regroupement qu'AIRSI, mais sans nom FNE convenu :
        // l'envoyer reviendrait à inventer une taxe pour la DGI.
        var fiches = Fiches.Append(new SageTaxDefinition
        {
            Code = "AIB", Intitule = "AIB", Taux = 2m, Regroupement = "AIRSI", EdiCode = "VAT",
        });
        var catalogue = new TaxCatalogue(
            fiches,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["AIRSI"] = "AIRSI" });

        var resultat = TaxMapping.Read(
            Ligne(taxe2: 2m, code2: "AIB"), CodeTvaZero.Tvad, catalogue);

        Assert.Empty(resultat.CustomTaxes);
        var constat = Assert.Single(resultat.PrelevementsSansMapping);
        Assert.Contains("AIB", constat);
        Assert.Contains("AIRSI", constat);   // le groupe, et le voisin déjà mappé
        Assert.Contains("Fne:CustomTaxes", constat);
    }

    [Fact]
    public void Un_prelevement_non_mappe_bloque_la_piece()
    {
        var fiches = Fiches.Append(new SageTaxDefinition
        {
            Code = "AIB", Intitule = "AIB", Taux = 2m, Regroupement = "AIRSI",
        });
        var catalogue = new TaxCatalogue(
            fiches,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["AIRSI"] = "AIRSI" });

        var resultat = TaxMapping.Read(Ligne(taxe2: 2m, code2: "AIB"), catalogue: catalogue);

        Assert.NotEmpty(resultat.PrelevementsSansMapping);
    }

    [Fact]
    public void Sans_catalogue_l_AIRSI_reste_repris()
    {
        // Le comportement par défaut ne régresse pas quand F_TAXE n'est pas lue.
        var resultat = TaxMapping.Read(Ligne(taxe2: 1.5m, code2: "AIRSI"));

        Assert.Equal("AIRSI", Assert.Single(resultat.CustomTaxes).Name);
    }

    [Fact]
    public void Les_taux_de_TVA_ne_sont_pas_affectes_par_le_catalogue()
    {
        var catalogue = Catalogue(("AIRSI", "AIRSI"));

        Assert.Equal(["TVA"], TaxMapping.Read(Ligne(taxe1: 18m, code1: "TVA"), catalogue: catalogue).Taxes);
        Assert.Equal(["TVAB"], TaxMapping.Read(Ligne(taxe1: 9m, code1: "TVA"), catalogue: catalogue).Taxes);
    }

    [Fact]
    public void Un_taux_hors_nomenclature_du_groupe_TVA_reste_un_avertissement()
    {
        // 12 % sur le code TVA : ce n'est pas un prélèvement inconnu, c'est un
        // taux que la nomenclature FNE ignore.
        var resultat = TaxMapping.Read(
            Ligne(taxe1: 12m, code1: "TVA"), catalogue: Catalogue(("AIRSI", "AIRSI")));

        Assert.Empty(resultat.PrelevementsSansMapping);
        Assert.Single(resultat.Avertissements);
        Assert.Contains("regroupement « TVA »", resultat.Avertissements[0]);
    }

    [Fact]
    public void Un_code_absent_de_F_TAXE_n_a_pas_de_groupe()
    {
        Assert.Equal("", Catalogue().Groupe("INCONNU"));
        Assert.Empty(Catalogue().MappesDuMemeGroupe("INCONNU"));
    }
}
