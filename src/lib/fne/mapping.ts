import { FneField, guessField, HEADER_FIELDS, IGNORED_COLUMNS } from "./fields";
import { normalizeKey } from "@/lib/core/text";

/** Association champ du modele -> libelle de colonne du fichier source. */
export type ColumnMapping = Partial<Record<FneField, string>>;

export interface MappingResult {
  mapping: ColumnMapping;
  /** Colonnes du fichier qu'aucun alias n'a permis d'identifier. */
  unmapped: string[];
  /** Colonnes connues de l'export FNE mais sans equivalent dans Sage. */
  ignored: string[];
  /** Colonnes ignorees parce qu'un champ etait deja pourvu par une colonne precedente. */
  duplicates: Array<{ column: string; field: FneField; kept: string }>;
}

export function detectMapping(columns: string[]): MappingResult {
  const mapping: ColumnMapping = {};
  const unmapped: string[] = [];
  const ignored: string[] = [];
  const duplicates: MappingResult["duplicates"] = [];
  const ignoredKeys = new Set(IGNORED_COLUMNS.map((label) => normalizeKey(label)));

  for (const column of columns) {
    if (ignoredKeys.has(normalizeKey(column))) {
      ignored.push(column);
      continue;
    }
    const field = guessField(column);
    if (!field) {
      unmapped.push(column);
      continue;
    }
    const existing = mapping[field];
    if (existing) {
      duplicates.push({ column, field, kept: existing });
      continue;
    }
    mapping[field] = column;
  }

  return { mapping, unmapped, ignored, duplicates };
}

/** Champs strictement necessaires pour produire un document de vente Sage. */
export const REQUIRED_FIELDS: FneField[] = [
  "numeroFacture",
  "dateFacture",
  "clientNom",
];

export function missingRequiredFields(mapping: ColumnMapping): FneField[] {
  return REQUIRED_FIELDS.filter((field) => !mapping[field]);
}

export function isHeaderField(field: FneField): boolean {
  return HEADER_FIELDS.has(field);
}
