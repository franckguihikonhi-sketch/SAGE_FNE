/**
 * Type de tableau lu et erreur de lecture, isoles de toute dependance.
 *
 * Le pipeline et le lecteur navigateur s'appuient sur ce module : les
 * importer depuis `read.ts` embarquerait papaparse et exceljs dans le bundle web.
 */

export interface SourceTable {
  /** Libelles de colonnes, dans l'ordre du fichier. */
  columns: string[];
  /** Lignes de donnees. Les cles correspondent a `columns`. */
  rows: Array<Record<string, unknown>>;
  /** Format detecte, affiche a l'utilisateur. */
  format: "csv" | "xlsx" | "json";
  /** Nom de la feuille Excel exploitee, le cas echeant. */
  sheet?: string;
}

export class ReadError extends Error {}
