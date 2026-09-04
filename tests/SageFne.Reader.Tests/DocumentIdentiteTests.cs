using SageFne.Core.Data;
using SageFne.Core.Models.Sage;

namespace SageFne.Core.Tests;

/// <summary>
/// Une facture comptabilisée passe de DO_Type 6 à 7. Si l'identité du document
/// suivait ce changement, la même facture serait certifiée deux fois.
/// </summary>
public class DocumentIdentiteTests
{
    private static SageDocumentHeader Entete(short type, short docType, string piece = "1219") => new()
    {
        Domaine = 0,
        Type = type,
        DocType = docType,
        Piece = piece,
        Date = new DateTime(2025, 12, 3),
        Tiers = "4111SITASARL",
    };

    [Fact]
    public void La_comptabilisation_ne_change_pas_l_identite()
    {
        var avant = Entete(SageDocumentTypes.Facture, docType: 6);
        var apres = Entete(SageDocumentTypes.FactureComptabilisee, docType: 6);

        // C'est tout l'enjeu : le registre doit retrouver la même clé.
        Assert.Equal(avant.Identite, apres.Identite);
        Assert.Equal("0/6/1219", apres.Identite);
    }

    [Fact]
    public void Un_bon_de_livraison_de_meme_numero_a_une_autre_identite()
    {
        var facture = Entete(SageDocumentTypes.Facture, docType: 6);
        var livraison = Entete(SageDocumentTypes.BonLivraison, docType: 3);

        Assert.NotEqual(facture.Identite, livraison.Identite);
    }

    [Fact]
    public void Sans_DO_DocType_le_type_courant_prend_le_relais()
    {
        // Les dossiers où la colonne n'est pas alimentée ne doivent pas voir
        // toutes leurs pièces se confondre sous l'identité « 0 ».
        var entete = Entete(SageDocumentTypes.Facture, docType: 0);

        Assert.Equal(SageDocumentTypes.Facture, entete.TypeOrigine);
        Assert.Equal("0/6/1219", entete.Identite);
    }

    [Fact]
    public void Comptabilisee_se_reconnait()
    {
        Assert.True(Entete(SageDocumentTypes.FactureComptabilisee, 6).EstComptabilisee);
        Assert.False(Entete(SageDocumentTypes.Facture, 6).EstComptabilisee);
    }

    [Theory]
    [InlineData(SageDocumentTypes.Facture, true)]
    [InlineData(SageDocumentTypes.FactureComptabilisee, true)]
    [InlineData(SageDocumentTypes.BonLivraison, false)]
    [InlineData(SageDocumentTypes.BonRetour, false)]
    [InlineData(SageDocumentTypes.Devis, false)]
    public void Seules_les_factures_sont_candidates(short type, bool attendu)
    {
        Assert.Equal(attendu, SageDocumentTypes.EstFacture(type));
    }

    [Fact]
    public void Le_bon_de_retour_dit_pourquoi_il_est_ecarte()
    {
        // Un avoir certifié comme une vente facturerait au client ce qu'il rend.
        var raison = SageDocumentTypes.RaisonExclusion(SageDocumentTypes.BonRetour);

        Assert.Contains("avoir", raison);
        Assert.Contains("rendre", raison);
    }

    [Fact]
    public void Le_doublon_dangereux_est_celui_qui_porte_6_et_7()
    {
        var memeFacture = new SageDocumentDuplicate
        {
            Piece = "1219", Nombre = 2, Types = [6, 7], DocTypes = [6],
        };
        var souchesCroisees = new SageDocumentDuplicate
        {
            Piece = "1219", Nombre = 2, Types = [3, 6], DocTypes = [3, 6],
        };

        Assert.True(memeFacture.MemeFacture);
        Assert.False(souchesCroisees.MemeFacture);
    }

    [Fact]
    public void L_inventaire_des_doublons_reste_une_lecture()
    {
        Assert.Equal(
            SageInvoiceRepository.SqlPiecesMultiTypes.Trim(),
            ReadOnlyGuard.Verify(SageInvoiceRepository.SqlPiecesMultiTypes));
    }

    [Fact]
    public void L_inventaire_des_doublons_groupe_sur_le_numero_seul()
    {
        var sql = string.Join(" ", SageInvoiceRepository.SqlPiecesMultiTypes
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains("group by e.DO_Piece", sql);
        Assert.Contains("having count(distinct e.DO_Type) > 1", sql);
    }
}
