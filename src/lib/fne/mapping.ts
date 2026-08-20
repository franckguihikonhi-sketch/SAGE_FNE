import { FneField, guessField, HEADER_FIELDS } from "./fields";

/** Association champ du modele -> libelle de colonne du fichier source. */
export type ColumnMapping = Partial<Record<FneField, string>>;

export interface MappingResult {
  mapping: ColumnMapping;
  /** Colonnes du fichier qu'aucun alias n'a permis d'identifier. */
  unmapped: string[];
  /** Colonnes ignorees parce qu'un champ etait deja pourvu par une colonne precedente. */
  duplicates: Array<{ column: string; field: FneField; kept: string }>;
}

export function detectMapping(columns: string[]): MappingResult {
  const mapping: ColumnMapping = {};
  const unmapped: string[] = [];
  const duplicates: MappingResult["duplicates"] = [];

  for (const column of columns) {
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

  return { mapping, unmapped, duplicates };
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
