import { normalizeKey } from "@/lib/core/text";

/**
 * Codes taxe de la plateforme FNE.
 *
 * Source : "Procedure de certification des factures des entreprises par API"
 * (DGI, mai 2025), annexe 1 - lexique du parametre `taxes`.
 */
export interface TaxCode {
  code: string;
  libelle: string;
  taux: number;
}

export const FNE_TAX_CODES: TaxCode[] = [
  { code: "TVA", libelle: "TVA normal 18 %", taux: 18 },
  { code: "TVAB", libelle: "TVA reduit 9 %", taux: 9 },
  { code: "TVAC", libelle: "TVA exec conv 0 %", taux: 0 },
  { code: "TVAD", libelle: "TVA exec leg 0 % (TEE et RME)", taux: 0 },
];

const INDEX = new Map<string, TaxCode>();
for (const tax of FNE_TAX_CODES) INDEX.set(normalizeKey(tax.code), tax);

export function findTaxCode(value: string): TaxCode | null {
  const key = normalizeKey(value);
  if (!key) return null;
  const direct = INDEX.get(key);
  if (direct) return direct;
  // Les exports portent parfois le libelle complet renvoye par l'API,
  // par exemple "TVA normal - TVA sur HT 18,00% - A".
  for (const tax of FNE_TAX_CODES) {
    if (key.startsWith(`${normalizeKey(tax.code)} `)) return tax;
  }
  return null;
}

/** Taux de TVA admis par la nomenclature FNE. */
export const TAUX_FNE: number[] = [18, 9, 0];

/** Un taux hors nomenclature revele une facture a plusieurs taux ou une donnee erronee. */
export function isTauxFne(taux: number, tolerance = 0.01): boolean {
  return TAUX_FNE.some((reference) => Math.abs(reference - taux) <= tolerance);
}

/** Deduit le code taxe FNE a partir d'un taux, pour les exports qui n'exposent que le taux. */
export function taxCodeFromRate(rate: number): string {
  const match = FNE_TAX_CODES.find((tax) => tax.taux === rate && tax.taux !== 0);
  if (match) return match.code;
  return rate === 0 ? "TVAC" : "";
}
