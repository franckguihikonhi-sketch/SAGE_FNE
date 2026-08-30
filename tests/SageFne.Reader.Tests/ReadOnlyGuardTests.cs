using SageFne.Reader.Data;

namespace SageFne.Reader.Tests;

public class ReadOnlyGuardTests
{
    [Theory]
    [InlineData("update F_DOCENTETE set DO_Statut = 1")]
    [InlineData("delete from F_DOCLIGNE")]
    [InlineData("drop table F_TAXE")]
    [InlineData("exec sp_who")]
    [InlineData("select 1; delete from F_DOCLIGNE")]
    public void Toute_ecriture_est_refusee(string sql)
    {
        Assert.Throws<InvalidOperationException>(() => ReadOnlyGuard.Verify(sql));
    }

    [Fact]
    public void Un_select_passe()
    {
        var sql = "select DO_Piece from F_DOCENTETE where DO_Piece = @piece";
        Assert.Equal(sql, ReadOnlyGuard.Verify(sql));
    }
}
