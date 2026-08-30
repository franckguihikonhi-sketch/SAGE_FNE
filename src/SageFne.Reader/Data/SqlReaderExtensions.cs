using System.Data.Common;

namespace SageFne.Reader.Data;

/// <summary>
/// Lecture tolérante des colonnes Sage.
/// </summary>
/// <remarks>
/// Les montants de Sage ne sont pas tous du même type SQL — numeric ici, float
/// là — et beaucoup de colonnes texte acceptent NULL. Passer par
/// <see cref="Convert"/> évite d'avoir à connaître le type exact de chaque
/// colonne pour lire une valeur qui, elle, est sans ambiguïté.
/// </remarks>
internal static class SqlReaderExtensions
{
    public static string Text(this DbDataReader reader, string colonne)
    {
        var valeur = reader[colonne];
        return valeur is DBNull or null ? "" : Convert.ToString(valeur)?.Trim() ?? "";
    }

    public static decimal Amount(this DbDataReader reader, string colonne)
    {
        var valeur = reader[colonne];
        return valeur is DBNull or null ? 0m : Convert.ToDecimal(valeur);
    }

    public static short Small(this DbDataReader reader, string colonne)
    {
        var valeur = reader[colonne];
        return valeur is DBNull or null ? (short)0 : Convert.ToInt16(valeur);
    }

    public static int Whole(this DbDataReader reader, string colonne)
    {
        var valeur = reader[colonne];
        return valeur is DBNull or null ? 0 : Convert.ToInt32(valeur);
    }

    public static DateTime Moment(this DbDataReader reader, string colonne)
    {
        var valeur = reader[colonne];
        return valeur is DBNull or null ? default : Convert.ToDateTime(valeur);
    }

    /// <summary>Un entier court qui peut légitimement ne pas être renseigné.</summary>
    public static short? SmallOrNull(this DbDataReader reader, string colonne)
    {
        var valeur = reader[colonne];
        return valeur is DBNull or null ? null : Convert.ToInt16(valeur);
    }

    /// <summary>Une date absente reste absente : elle ne devient pas 01/01/0001.</summary>
    public static DateTime? MomentOrNull(this DbDataReader reader, string colonne)
    {
        var valeur = reader[colonne];
        return valeur is DBNull or null ? null : Convert.ToDateTime(valeur);
    }

    public static bool Flag(this DbDataReader reader, string colonne)
    {
        var valeur = reader[colonne];
        return valeur is not (DBNull or null) && Convert.ToInt32(valeur) != 0;
    }
}
