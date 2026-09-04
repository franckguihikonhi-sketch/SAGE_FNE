using System.Data.Common;

namespace SageFne.Core.Data;

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
        return valeur switch
        {
            DBNull or null => "",
            // Les colonnes « cb… » de Sage sont des varbinary de réplication.
            // Convert.ToString en tire « System.Byte[] », qui n'apprend rien :
            // l'hexadécimal, lui, se lit.
            byte[] octets => Hexadecimal(octets),
            _ => Convert.ToString(valeur)?.Trim() ?? "",
        };
    }

    private static string Hexadecimal(byte[] octets)
    {
        if (octets.Length == 0) return "";
        const int apercu = 12;
        var tete = Convert.ToHexString(octets.AsSpan(0, Math.Min(apercu, octets.Length)));
        return octets.Length <= apercu
            ? $"0x{tete}"
            : $"0x{tete}… ({octets.Length} octets)";
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

    // --- Colonnes qui peuvent ne pas exister dans le dossier ----------------
    //
    // Une colonne absente de la table n'a pas été demandée dans le select : la
    // lire lèverait IndexOutOfRange. Ces surcharges rendent la valeur par
    // défaut, exactement comme une colonne présente mais nulle.

    public static string Text(this DbDataReader reader, ColonnesTable colonnes, string nom) =>
        colonnes.A(nom) ? reader.Text(nom) : "";

    public static decimal Amount(this DbDataReader reader, ColonnesTable colonnes, string nom) =>
        colonnes.A(nom) ? reader.Amount(nom) : 0m;

    public static short Small(this DbDataReader reader, ColonnesTable colonnes, string nom) =>
        colonnes.A(nom) ? reader.Small(nom) : (short)0;

    public static int Whole(this DbDataReader reader, ColonnesTable colonnes, string nom) =>
        colonnes.A(nom) ? reader.Whole(nom) : 0;

    public static DateTime Moment(this DbDataReader reader, ColonnesTable colonnes, string nom) =>
        colonnes.A(nom) ? reader.Moment(nom) : default;

    public static bool Flag(this DbDataReader reader, ColonnesTable colonnes, string nom) =>
        colonnes.A(nom) && reader.Flag(nom);
}
