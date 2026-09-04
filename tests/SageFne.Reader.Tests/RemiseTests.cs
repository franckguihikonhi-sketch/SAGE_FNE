using SageFne.Core.Mapping;
using SageFne.Core.Models.Sage;

namespace SageFne.Core.Tests;

/// <summary>
/// Envoyer le prix brut d'une ligne remisée ferait certifier plus que ce que le
/// client a payé. Ces tests tiennent le prix net.
/// </summary>
public class RemiseTests
{
    private static SageDocumentLine Ligne(
        decimal quantite,
        decimal prixUnitaire,
        decimal montantHT,
        decimal remise = 0m,
        short type = SageRemise.Pourcentage) => new()
    {
        Domaine = 0,
        Type = 6,
        Piece = "1223",
        Ligne = 1,
        Quantite = quantite,
        PrixUnitaire = prixUnitaire,
        MontantHT = montantHT,
        Remise1 = remise,
        Remise1Type = type,
    };

    [Fact]
    public void Sans_remise_le_prix_unitaire_ne_bouge_pas()
    {
        var resultat = RemiseMapping.Read(Ligne(quantite: 10m, prixUnitaire: 5000m, montantHT: 50000m));

        Assert.False(resultat.Remisee);
        Assert.Equal(5000m, resultat.PrixUnitaireNet);
        Assert.Empty(resultat.Avertissements);
    }

    [Fact]
    public void Une_remise_en_pourcentage_donne_le_prix_net()
    {
        var resultat = RemiseMapping.Read(
            Ligne(quantite: 10m, prixUnitaire: 5000m, montantHT: 45000m, remise: 10m));

        Assert.True(resultat.Remisee);
        Assert.Equal(4500m, resultat.PrixUnitaireNet);
        Assert.Equal(500m, resultat.RemiseUnitaire);
        Assert.True(resultat.Concordante);
        Assert.Empty(resultat.Avertissements);
    }

    [Fact]
    public void Une_remise_en_montant_donne_le_prix_net()
    {
        // « 200 » sur une ligne à 2000 : dix pour cent si c'est un pourcentage,
        // 1800 si c'est un montant. Seul le type permet de trancher.
        var resultat = RemiseMapping.Read(
            Ligne(quantite: 5m, prixUnitaire: 2000m, montantHT: 9000m,
                remise: 200m, type: SageRemise.Montant));

        Assert.Equal(1800m, resultat.PrixUnitaireNet);
        Assert.True(resultat.Concordante);
    }

    [Fact]
    public void Le_meme_nombre_ne_donne_pas_le_meme_prix_selon_le_type()
    {
        // La démonstration que lire DL_Remise0NREM_Type était nécessaire.
        var pourcentage = RemiseMapping.Read(
            Ligne(quantite: 5m, prixUnitaire: 2000m, montantHT: 9900m,
                remise: 1m, type: SageRemise.Pourcentage));
        var montant = RemiseMapping.Read(
            Ligne(quantite: 5m, prixUnitaire: 2000m, montantHT: 9995m,
                remise: 1m, type: SageRemise.Montant));

        Assert.Equal(1980m, pourcentage.PrixUnitaireNet);
        Assert.Equal(1999m, montant.PrixUnitaireNet);
        Assert.True(pourcentage.Concordante);
        Assert.True(montant.Concordante);
    }

    [Fact]
    public void Un_desaccord_avec_Sage_est_signale_mais_c_est_Sage_qui_est_envoye()
    {
        // Une remise de 10 % sur 5000 donnerait 4500 ; Sage retient 4000. Notre
        // lecture du type est donc fausse quelque part — le prix envoyé reste
        // celui de Sage, mais le contrôle doit le dire.
        var resultat = RemiseMapping.Read(
            Ligne(quantite: 10m, prixUnitaire: 5000m, montantHT: 40000m, remise: 10m));

        Assert.Equal(4000m, resultat.PrixUnitaireNet);
        Assert.False(resultat.Concordante);
        Assert.Single(resultat.Avertissements);
        Assert.Contains("C'est le chiffre de Sage qui est envoyé", resultat.Avertissements[0]);
    }

    [Fact]
    public void Un_type_de_remise_inconnu_ne_passe_pas_en_silence()
    {
        var resultat = RemiseMapping.Read(
            Ligne(quantite: 10m, prixUnitaire: 5000m, montantHT: 45000m, remise: 10m, type: 7));

        Assert.Equal(4500m, resultat.PrixUnitaireNet);   // Sage fait toujours foi
        Assert.False(resultat.Concordante);
        Assert.Contains("type 7", resultat.Avertissements[0]);
    }

    [Fact]
    public void Sans_quantite_le_prix_ne_peut_pas_etre_recoupe()
    {
        var resultat = RemiseMapping.Read(
            Ligne(quantite: 0m, prixUnitaire: 5000m, montantHT: 0m, remise: 10m));

        Assert.Equal(4500m, resultat.PrixUnitaireNet);   // la cascade, faute de mieux
        Assert.False(resultat.Concordante);
        Assert.Contains("recoupé", resultat.Avertissements[0]);
    }

    [Fact]
    public void Les_trois_remises_s_appliquent_en_cascade()
    {
        // 5000, moins 10 %, moins 100 F, moins 5 % : 4500, 4400, 4180.
        var ligne = new SageDocumentLine
        {
            Domaine = 0, Type = 6, Piece = "1223", Ligne = 1,
            Quantite = 10m, PrixUnitaire = 5000m, MontantHT = 41800m,
            Remise1 = 10m, Remise1Type = SageRemise.Pourcentage,
            Remise2 = 100m, Remise2Type = SageRemise.Montant,
            Remise3 = 5m, Remise3Type = SageRemise.Pourcentage,
        };

        var resultat = RemiseMapping.Read(ligne);

        Assert.Equal(4180m, resultat.PrixUnitaireNet);
        Assert.True(resultat.Concordante);
        Assert.Equal("10 % puis 100 (montant) puis 5 %", resultat.Description);
    }
}
