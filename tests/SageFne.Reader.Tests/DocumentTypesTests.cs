using SageFne.Reader.Batch;
using SageFne.Reader.Data;

namespace SageFne.Reader.Tests;

/// <summary>
/// Le diagnostic des types de documents doit répondre à une question sans en
/// poser une autre : il regarde tout le domaine des ventes, et il n'écrit rien.
/// </summary>
public class DocumentTypesTests
{
    private static string Aplatir(string sql) =>
        string.Join(" ", sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void L_inventaire_ne_filtre_pas_sur_un_type()
    {
        var sql = Aplatir(SageInvoiceRepository.SqlTypesDocuments);

        // Filtrer sur DO_Type = 6 répondrait « 6 » à la question « quels
        // types ? » : le domaine borne la lecture, le type non.
        Assert.Contains("where e.DO_Domaine = @domaine", sql);
        Assert.Contains("group by e.DO_Type", sql);
        Assert.DoesNotContain("DO_Type = @type", sql);
    }

    [Fact]
    public void Les_exemples_sont_pris_par_type()
    {
        var sql = Aplatir(SageInvoiceRepository.SqlExemplesTypes(avecDocType: false));

        Assert.Contains("partition by e.DO_Type", sql);
        Assert.Contains("where Rang <= @exemples", sql);
        Assert.DoesNotContain("DO_DocType", sql);
    }

    [Fact]
    public void DO_DocType_n_est_lue_que_si_la_colonne_existe()
    {
        // Toutes les versions du dossier ne l'ont pas : la demander à l'aveugle
        // ferait échouer le diagnostic là où il est le plus utile.
        Assert.Contains("e.DO_DocType", SageInvoiceRepository.SqlExemplesTypes(avecDocType: true));
        Assert.Contains("COLUMN_NAME = @colonne", SageInvoiceRepository.SqlColonneExiste);
    }

    [Fact]
    public void Les_trois_requetes_restent_des_lectures()
    {
        foreach (var sql in new[]
                 {
                     SageInvoiceRepository.SqlTypesDocuments,
                     SageInvoiceRepository.SqlColonneExiste,
                     SageInvoiceRepository.SqlExemplesTypes(avecDocType: true),
                     SageInvoiceRepository.SqlExemplesTypes(avecDocType: false),
                 })
        {
            Assert.Equal(sql.Trim(), ReadOnlyGuard.Verify(sql));
        }
    }

    [Fact]
    public void La_commande_doctypes_ne_devient_pas_un_numero_de_piece()
    {
        var ligne = CommandLine.Parse(["doctypes"]);

        Assert.Equal(Verbe.TypesDocuments, ligne.Verbe);
        Assert.Empty(ligne.Query.Pieces);
        Assert.Empty(ligne.Erreurs);
    }

    [Fact]
    public void Sans_verbe_la_ligne_de_commande_reste_un_dry_run()
    {
        Assert.Equal(Verbe.DryRun, CommandLine.Parse(["1219"]).Verbe);
    }

    [Fact]
    public async Task Le_jeu_d_essai_montre_plusieurs_types()
    {
        var types = await new DemoSageInvoiceRepository().GetDocumentTypesAsync();

        Assert.True(types.Count > 1, "le diagnostic n'aurait rien à montrer avec un seul type");
        Assert.Equal(types.Select(type => type.Type).OrderBy(type => type), types.Select(type => type.Type));

        var factures = types.Single(type => type.Type == 6);
        Assert.Equal("Facture", factures.LibelleUsuel);
        Assert.Equal(5, factures.Nombre);
        Assert.All(types, type => Assert.True(type.Exemples.Count <= 5));
        Assert.All(types, type => Assert.True(type.Exemples.Count <= type.Nombre));
    }

    [Fact]
    public async Task Le_nombre_d_exemples_demande_est_respecte()
    {
        var types = await new DemoSageInvoiceRepository().GetDocumentTypesAsync(exemplesParType: 1);

        Assert.All(types, type => Assert.Single(type.Exemples));
    }
}
