import { cleanCell } from "./text";

/**
 * Parse une date d'export FNE et retourne une date ISO (yyyy-mm-dd).
 * Formats acceptes : dd/mm/yyyy, dd-mm-yyyy, yyyy-mm-dd, dd/mm/yy,
 * ISO complet avec heure, et serie Excel (nombre de jours depuis 1899-12-30).
 */
export function parseDate(value: unknown): string | null {
  if (value instanceof Date && !Number.isNaN(value.getTime())) {
    return toIso(value.getUTCFullYear(), value.getUTCMonth() + 1, value.getUTCDate());
  }

  if (typeof value === "number" && Number.isFinite(value)) {
    return fromExcelSerial(value);
  }

  const raw = cleanCell(value);
  if (!raw) return null;

  // Serie Excel arrivee sous forme de chaine
  if (/^\d{5}(\.\d+)?$/.test(raw)) return fromExcelSerial(Number(raw));

  const iso = raw.match(/^(\d{4})[-/](\d{1,2})[-/](\d{1,2})/);
  if (iso) return toIso(Number(iso[1]), Number(iso[2]), Number(iso[3]));

  const fr = raw.match(/^(\d{1,2})[-/.](\d{1,2})[-/.](\d{2,4})/);
  if (fr) {
    let year = Number(fr[3]);
    if (year < 100) year += year < 70 ? 2000 : 1900;
    return toIso(year, Number(fr[2]), Number(fr[1]));
  }

  const parsed = new Date(raw);
  if (!Number.isNaN(parsed.getTime())) {
    return toIso(parsed.getFullYear(), parsed.getMonth() + 1, parsed.getDate());
  }
  return null;
}

function toIso(year: number, month: number, day: number): string | null {
  if (month < 1 || month > 12 || day < 1 || day > 31) return null;
  return `${String(year).padStart(4, "0")}-${String(month).padStart(2, "0")}-${String(day).padStart(2, "0")}`;
}

function fromExcelSerial(serial: number): string | null {
  // Excel compte les jours depuis le 1899-12-30 (bug de l'annee bissextile 1900 inclus).
  const ms = Math.round(serial * 86400000);
  const date = new Date(Date.UTC(1899, 11, 30) + ms);
  if (Number.isNaN(date.getTime())) return null;
  return toIso(date.getUTCFullYear(), date.getUTCMonth() + 1, date.getUTCDate());
}

/** Formate une date ISO selon le masque attendu par le format d'import Sage. */
export function formatDate(iso: string, format: "DDMMYYYY" | "DD/MM/YYYY" | "YYYYMMDD" | "DDMMYY"): string {
  const [y = "", m = "", d = ""] = iso.split("-");
  switch (format) {
    case "DDMMYYYY":
      return `${d}${m}${y}`;
    case "DD/MM/YYYY":
      return `${d}/${m}/${y}`;
    case "YYYYMMDD":
      return `${y}${m}${d}`;
    case "DDMMYY":
      return `${d}${m}${y.slice(2)}`;
  }
}
