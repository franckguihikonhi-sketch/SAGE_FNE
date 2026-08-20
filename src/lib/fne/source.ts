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

/**
 * Message oppose au PDF de facture certifiee.
 *
 * FNE propose trois exports : le JSON, le tableur et le PDF de la facture.
 * Le PDF est lisible - ses polices portent une table ToUnicode - mais tous ses
 * montants sont arrondis au franc a l'impression : la facture 2304903U26000000889
 * y affiche un prix unitaire de 1 077 et un total HT de 21 546 la ou FNE a
 * certifie 1077,2763 et 21545,526. L'importer creerait un ecart comptable sur
 * chaque ligne. C'est le document legal, pas une source d'integration.
 */
export const MESSAGE_PDF =
  "Le PDF est la facture certifiee destinee a etre lue ou archivee : ses montants y sont " +
  "arrondis au franc, un import creerait un ecart sur chaque ligne. Exportez les factures " +
  "au format JSON depuis FNE, seul format qui porte les montants certifies et le detail des articles.";
