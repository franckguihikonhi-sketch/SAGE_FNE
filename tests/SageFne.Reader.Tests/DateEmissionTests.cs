using Microsoft.Extensions.Options;
using SageFne.Core.Configuration;
using SageFne.Core.Mapping;
using SageFne.Core.Models.Sage;
using SageFne.Core.Validation;

namespace SageFne.Core.Tests;

/// <summary>
/// La date d'émission n'est pas transmise, et cela doit se dire.
/// </summary>
/// <remarks>
/// Le corps envoyé à la DGI porte quinze champs, et aucun n'est une date. La
/// facture est donc certifiée à la date du dépôt, pas à celle du document Sage.
///
/// Tant que l'émission et le dépôt tombent le même jour, l'écart est nul et
/// invisible. Un agent arrêté un week-end, une correction reprise le
/// surlendemain, et la DGI certifie sous une date qui n'est pas celle de la
/// facture — un écart fiscal, pas un détail d'affichage.
///
/// Aucun champ n'est inventé pour y remédier : le nom qu'attendrait la
/// plateforme n'est pas connu, et le supposer serait pire que le manque. La
/// question est posée à la DGI ; en attendant, l'écart est signalé.
/// </remarks>
public class DateEmissionTests
{
    private static readonly FneInvoiceMapper Mappeur = new(Options.Create(new FneOptions
    {
        Template = "B2B",
        PaymentMethod = "deferred",
        PointOfSale = "FISH-AFRIC",
        Establishment = "FISH-AFRIC",
    }));

    private static CheckReport Convertir(DateTime dateDeLaPiece)
    {
        var rapport = new CheckReport();
        var entete = new SageDocumentHeader
        {
            Domaine = 0, Type = 6, Piece = "1222",
            Date = dateDeLaPiece, Tiers = "4111ABAL",
        };
        var lignes = new List<SageDocumentLine>
        {
            new()
            {
                Domaine = 0, Type = 6, Piece = "1222", Ligne = 1,
                ArticleReference = "24PG001", Designation = "Panga 400-1000 GR",
                Quantite = 7m, PrixUnitaire = 16000m, Unite = "CN",
                MontantHT = 112000m, MontantTTC = 122080m, Taxe1 = 9m,
            },
        };
        var client = new SageCustomer
        {
            CtNum = "4111ABAL", Intitule = "ABALO",
            Identifiant = "478925k", Telephone = "0151838382",
        };

        Mappeur.Map(entete, lignes, client, rapport);
        return rapport;
    }

    [Fact]
    public void Le_corps_FNE_ne_porte_aucune_date()
    {
        // Le fait brut dont tout le reste découle. S'il change — la DGI
        // publiant un champ de date — ce test tombe, et c'est ce qu'on veut :
        // il faudra alors le remplir plutôt que signaler son absence.
        var champs = typeof(Core.Models.Fne.FneInvoice)
            .GetProperties()
            .Select(propriete => propriete.Name)
            .ToList();

        Assert.DoesNotContain(champs, nom => nom.Contains("Date", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Une_piece_du_jour_ne_declenche_aucun_avertissement_de_date()
    {
        // L'écart est nul : le signaler serait du bruit sur chaque facture.
        var rapport = Convertir(DateTime.Today);

        Assert.DoesNotContain(rapport.Constats, c => c.Code == "DATE_EMISSION_NON_TRANSMISE");
    }

    [Fact]
    public void Une_piece_de_la_veille_signale_l_ecart()
    {
        var rapport = Convertir(DateTime.Today.AddDays(-1));

        var constat = Assert.Single(
            rapport.Constats.Where(c => c.Code == "DATE_EMISSION_NON_TRANSMISE"));

        Assert.Equal(Severite.Avertissement, constat.Severite);
        Assert.Contains("1 jour(s) d'écart", constat.Message);
    }

    [Fact]
    public void Un_ecart_de_plusieurs_jours_est_compte_juste()
    {
        var rapport = Convertir(DateTime.Today.AddDays(-3));

        Assert.Contains(
            rapport.Constats,
            c => c.Code == "DATE_EMISSION_NON_TRANSMISE" && c.Message.Contains("3 jour(s)"));
    }

    [Fact]
    public void L_ecart_ne_bloque_pas_la_piece()
    {
        // Un avertissement, pas une erreur : la facture peut partir, et c'est
        // à un humain de juger si la date du dépôt convient. Bloquer ferait
        // d'un doute une panne.
        var rapport = Convertir(DateTime.Today.AddDays(-5));

        Assert.DoesNotContain(
            rapport.Constats,
            c => c.Code == "DATE_EMISSION_NON_TRANSMISE" && c.Severite == Severite.Erreur);
    }
}
