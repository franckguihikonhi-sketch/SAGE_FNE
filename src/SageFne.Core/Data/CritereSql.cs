using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;

namespace SageFne.Core.Data;

/// <summary>
/// Construit le filtre d'un lot : pièces nommées, période, ou les deux.
/// </summary>
/// <remarks>
/// Seuls des <b>noms</b> de paramètres sont engendrés — @piece0, @piece1… —
/// jamais une valeur. Rien de ce qui vient de l'extérieur n'entre dans le
/// texte de la requête.
/// </remarks>
/// <param name="alias">Alias de F_DOCENTETE dans la requête appelante.</param>
internal sealed class CritereSql(string alias)
{
    public string Where(InvoiceQuery query)
    {
        var clauses = new StringBuilder();

        if (query.Pieces.Count > 0)
        {
            var noms = query.Pieces.Select((_, rang) => $"@piece{rang}");
            clauses.Append($"  and {alias}.DO_Piece in ({string.Join(", ", noms)})\n");
        }

        if (query.Depuis is not null) clauses.Append($"  and {alias}.DO_Date >= @depuis\n");
        // Borne haute exclue : une pièce datée du 31 à 23 h reste dans le lot.
        if (query.Jusqua is not null) clauses.Append($"  and {alias}.DO_Date < @jusqua\n");

        return clauses.ToString();
    }

    public void Appliquer(SqlCommand commande, InvoiceQuery query)
    {
        for (var rang = 0; rang < query.Pieces.Count; rang++)
        {
            commande.Parameters.Add($"@piece{rang}", SqlDbType.VarChar, 50).Value = query.Pieces[rang];
        }

        if (query.Depuis is not null)
        {
            commande.Parameters.Add("@depuis", SqlDbType.DateTime).Value = query.Depuis.Value;
        }

        if (query.Jusqua is not null)
        {
            commande.Parameters.Add("@jusqua", SqlDbType.DateTime).Value = query.Jusqua.Value;
        }
    }
}
