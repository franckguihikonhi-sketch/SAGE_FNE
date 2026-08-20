/**
 * Utilitaires de normalisation de texte partages par le parseur FNE
 * et le generateur de fichier Sage.
 */

/** Espaces insecables utilises par les exports Excel/CSV francophones. */
const NBSP = /[   ]/g;
const ANY_SPACE = /[\s   ]/g;

/** Retire les accents, la casse et la ponctuation d'un libelle de colonne. */
export function normalizeKey(value: string): string {
  return value
    // Le signe % porte du sens ("Remise (%)" vs "Remise") : on le conserve
    // sous forme de mot avant de retirer la ponctuation.
    .replace(/%/g, " pct ")
    .normalize("NFD")
    .replace(/[̀-ͯ]/g, "")
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, " ")
    .trim();
}

export function cleanCell(value: unknown): string {
  if (value === null || value === undefined) return "";
  if (value instanceof Date) return value.toISOString().slice(0, 10);
  return String(value).replace(NBSP, " ").trim();
}

/**
 * Parse un montant tel qu'il sort d'un export FR : "1 234 567,89", "1.234.567,89",
 * "12 500 FCFA", "(1 200)" (negatif comptable), "-1 200,50".
 * Retourne `null` si la cellule ne contient aucun nombre exploitable.
 */
export function parseAmount(value: unknown): number | null {
  if (typeof value === "number") return Number.isFinite(value) ? value : null;
  let raw = cleanCell(value);
  if (!raw) return null;

  let negative = false;
  if (/^\(.*\)$/.test(raw)) {
    negative = true;
    raw = raw.slice(1, -1);
  }

  // Supprime devises et suffixes : FCFA, XOF, F CFA, %, etc.
  raw = raw.replace(/(fcfa|xof|f\s?cfa|frs?|francs?|%)/gi, "");
  raw = raw.replace(ANY_SPACE, "");
  if (!raw) return null;

  if (raw.startsWith("-")) {
    negative = !negative;
    raw = raw.slice(1);
  } else if (raw.startsWith("+")) {
    raw = raw.slice(1);
  }

  const lastComma = raw.lastIndexOf(",");
  const lastDot = raw.lastIndexOf(".");
  let decimalSep: string | null = null;
  if (lastComma !== -1 && lastDot !== -1) {
    decimalSep = lastComma > lastDot ? "," : ".";
  } else if (lastComma !== -1) {
    // "1,234" est ambigu. Trois chiffres apres le separateur => separateur de milliers.
    decimalSep = raw.length - lastComma - 1 === 3 ? null : ",";
  } else if (lastDot !== -1) {
    decimalSep = raw.length - lastDot - 1 === 3 ? null : ".";
  }

  let normalized: string;
  if (decimalSep) {
    const cut = raw.lastIndexOf(decimalSep);
    const intPart = raw.slice(0, cut).replace(/[.,]/g, "");
    const decPart = raw.slice(cut + 1).replace(/[.,]/g, "");
    normalized = `${intPart}.${decPart}`;
  } else {
    normalized = raw.replace(/[.,]/g, "");
  }

  if (normalized === "" || normalized === "." || !/^\d*(\.\d+)?$/.test(normalized)) return null;
  const parsed = Number(normalized);
  if (!Number.isFinite(parsed)) return null;
  return negative ? -parsed : parsed;
}

/** Parse un taux : "18", "18%", "18,00 %", "0,18" -> 18. */
export function parseRate(value: unknown): number | null {
  const amount = parseAmount(value);
  if (amount === null) return null;
  // Un taux exprime en fraction (0,18) est ramene en pourcentage.
  if (amount > 0 && amount < 1) return round(amount * 100, 4);
  return amount;
}

export function round(value: number, decimals = 2): number {
  const factor = 10 ** decimals;
  return Math.round((value + Number.EPSILON) * factor) / factor;
}

/** Formate un nombre pour le fichier Sage (separateur decimal configurable). */
export function formatNumber(value: number, decimals: number, separator: "." | ","): string {
  const text = value.toFixed(decimals);
  return separator === "," ? text.replace(".", ",") : text;
}
