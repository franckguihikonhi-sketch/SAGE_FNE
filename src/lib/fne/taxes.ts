import { normalizeKey } from "@/lib/core/text";

/**
 * Correspondance entre les codes taxe de la plateforme FNE et les taux.
 *
 * A CONFIRMER sur un export reel : les codes ci-dessous suivent la
 * nomenclature publiee par la DGI (TVA au taux normal, taux reduit,
 * exonerations conventionnelles et legales). Toute valeur inconnue est
 * remontee comme anomalie plutot que silencieusement ramenee a 0 %.
 */
export interface TaxCode {
  code: string;
  libelle: string;
  taux: number;
}

export const FNE_TAX_CODES: TaxCode[] = [
  { code: "TVA", libelle: "TVA taux normal", taux: 18 },
  { code: "TVAB", libelle: "TVA taux reduit", taux: 9 },
  { code: "TVAC", libelle: "Exoneration conventionnelle", taux: 0 },
  { code: "TVAD", libelle: "Exoneration legale", taux: 0 },
  { code: "EXO", libelle: "Exonere", taux: 0 },
  { code: "TEE", libelle: "Taxe sur les entreprises exonerees", taux: 0 },
];

const INDEX = new Map<string, TaxCode>();
for (const tax of FNE_TAX_CODES) INDEX.set(normalizeKey(tax.code), tax);

export function findTaxCode(value: string): TaxCode | null {
  const key = normalizeKey(value);
  if (!key) return null;
  return INDEX.get(key) ?? null;
}

/** Deduit le code taxe FNE a partir d'un taux, pour les exports qui n'exposent que le taux. */
export function taxCodeFromRate(rate: number): string {
  const match = FNE_TAX_CODES.find((tax) => tax.taux === rate && tax.taux !== 0);
  if (match) return match.code;
  return rate === 0 ? "EXO" : "";
}
