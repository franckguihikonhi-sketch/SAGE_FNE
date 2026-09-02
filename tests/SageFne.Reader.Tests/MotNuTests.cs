using SageFne.Core.Data;
using SageFne.Reader.Batch;

namespace SageFne.Reader.Tests;

/// <summary>
/// Ce que devient un mot que la ligne de commande ne connaît pas.
/// </summary>
/// <remarks>
/// « dry-run » n'a jamais été une commande : le dry run est le verbe par défaut.
/// Le mot tombait donc dans les numéros de pièce, la requête filtrait sur une
/// pièce nommée « dry-run », et le CLI répondait « Aucune facture pour pièce(s)
/// dry-run, du 01/01/2025 ».
///
/// Cette phrase a été lue - à juste titre - comme « le dossier ne contient
/// aucune facture depuis 2025 », alors que l'agent en lisait deux au même
/// moment. Une commande mal tapée ne doit pas produire un constat sur les
/// données.
/// </remarks>
public class MotNuTests
{
    [Fact]
    public void Un_mot_sans_chiffre_n_est_pas_un_numero_de_piece()
    {
        var ligne = CommandLine.Parse(["nimportequoi", "--depuis", "2025-01-01"]);

        Assert.NotEmpty(ligne.Erreurs);
        Assert.Contains(ligne.Erreurs, e => e.Contains("nimportequoi"));
        Assert.Empty(ligne.Query.Pieces);
    }

    [Fact]
    public void Dry_run_est_desormais_le_nom_du_verbe_par_defaut()
    {
        var ligne = CommandLine.Parse(["dry-run", "--depuis", "2025-01-01"]);

        Assert.Empty(ligne.Erreurs);
        Assert.Equal(Verbe.DryRun, ligne.Verbe);

        // Et surtout : il ne filtre plus sur une pièce imaginaire.
        Assert.Empty(ligne.Query.Pieces);
        Assert.Equal(new DateTime(2025, 1, 1), ligne.Query.Depuis);
    }

    [Fact]
    public void Sans_commande_le_dry_run_reste_le_verbe_par_defaut()
    {
        var ligne = CommandLine.Parse(["--depuis", "2025-01-01"]);

        Assert.Empty(ligne.Erreurs);
        Assert.Equal(Verbe.DryRun, ligne.Verbe);
    }

    [Theory]
    [InlineData("1221")]
    [InlineData("FA1221")]
    [InlineData("2026-0001")]
    public void Un_numero_de_piece_porte_au_moins_un_chiffre_et_passe(string numero)
    {
        var ligne = CommandLine.Parse([numero]);

        Assert.Empty(ligne.Erreurs);
        Assert.Equal([numero], ligne.Query.Pieces);
    }

    [Fact]
    public void Un_lot_vide_filtre_sur_piece_nomme_le_filtre()
    {
        // Sans cette précision, « Aucune facture » est indiscernable d'un
        // dossier réellement vide.
        var requete = new InvoiceQuery { Pieces = ["9999"], Depuis = new DateTime(2025, 1, 1) };

        Assert.Contains("9999", requete.Describe());
    }
}
