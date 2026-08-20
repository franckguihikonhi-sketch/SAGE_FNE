import { describe, expect, it } from "vitest";
import { normalizeKey, parseAmount, parseRate, round } from "@/lib/core/text";
import { formatDate, parseDate } from "@/lib/core/date";

describe("parseAmount", () => {
  it("lit les montants au format francais", () => {
    expect(parseAmount("1 234 567,89")).toBe(1234567.89);
    expect(parseAmount("1.234.567,89")).toBe(1234567.89);
    expect(parseAmount("500 000")).toBe(500000);
    expect(parseAmount("12 500 FCFA")).toBe(12500);
  });

  it("lit les montants au format machine", () => {
    expect(parseAmount("1234567.89")).toBe(1234567.89);
    expect(parseAmount(4200)).toBe(4200);
    expect(parseAmount("1,234")).toBe(1234);
    expect(parseAmount("1,23")).toBe(1.23);
  });

  it("gere les negatifs", () => {
    expect(parseAmount("-59 000")).toBe(-59000);
    expect(parseAmount("(1 200)")).toBe(-1200);
  });

  it("retourne null sur une cellule vide ou non numerique", () => {
    expect(parseAmount("")).toBeNull();
    expect(parseAmount("N/A")).toBeNull();
    expect(parseAmount(null)).toBeNull();
  });

  it("neutralise les espaces insecables des exports Excel", () => {
    expect(parseAmount("1 234,50")).toBe(1234.5);
    expect(parseAmount("1 234,50")).toBe(1234.5);
  });
});

describe("parseRate", () => {
  it("normalise les taux", () => {
    expect(parseRate("18%")).toBe(18);
    expect(parseRate("18,00 %")).toBe(18);
    expect(parseRate("0,18")).toBe(18);
    expect(parseRate("9")).toBe(9);
  });
});

describe("parseDate", () => {
  it("lit les formats usuels", () => {
    expect(parseDate("12/08/2026")).toBe("2026-08-12");
    expect(parseDate("2026-08-12")).toBe("2026-08-12");
    expect(parseDate("12-08-26")).toBe("2026-08-12");
    expect(parseDate(new Date(Date.UTC(2026, 7, 12)))).toBe("2026-08-12");
  });

  it("lit une serie Excel", () => {
    expect(parseDate(46246)).toBe("2026-08-12");
  });

  it("formate pour Sage", () => {
    expect(formatDate("2026-08-12", "DDMMYYYY")).toBe("12082026");
    expect(formatDate("2026-08-12", "DD/MM/YYYY")).toBe("12/08/2026");
    expect(formatDate("2026-08-12", "YYYYMMDD")).toBe("20260812");
  });
});

describe("normalizeKey", () => {
  it("retire accents, casse et ponctuation", () => {
    expect(normalizeKey("Numéro de facture")).toBe("numero de facture");
    expect(normalizeKey("Désignation  (article)")).toBe("designation article");
  });
});

describe("round", () => {
  it("arrondit sans erreur de flottant", () => {
    expect(round(1.005, 2)).toBe(1.01);
    expect(round(229880.99999, 0)).toBe(229881);
  });
});
