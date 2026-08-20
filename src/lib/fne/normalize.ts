import { parseDate } from "@/lib/core/date";
import {
  DocumentKind,
  Invoice,
  InvoiceLine,
  emptyCustomer,
  emptyTotals,
} from "@/lib/core/model";
import { cleanCell, normalizeKey, parseAmount, parseRate, round } from "@/lib/core/text";
import { ColumnMapping } from "./mapping";
import { FneField } from "./fields";
import { findTaxCode, taxCodeFromRate } from "./taxes";
import { SourceTable } from "./read";

export interface NormalizeOptions {
  /**
   * Taux de TVA applique quand ni le code taxe ni le taux ne sont exploitables
   * et que la ligne porte une TVA non nulle.
   */
  tauxTvaParDefaut: number;
  /** Nombre de decimales des montants (0 pour le franc CFA). */
  decimales: number;
}

export const DEFAULT_NORMALIZE_OPTIONS: NormalizeOptions = {
  tauxTvaParDefaut: 18,
  decimales: 0,
};

export interface NormalizeResult {
  invoices: Invoice[];
  /** Anomalies non bloquantes rencontrees pendant la normalisation. */
  warnings: string[];
}

export function normalize(
  table: SourceTable,
  mapping: ColumnMapping,
  options: NormalizeOptions = DEFAULT_NORMALIZE_OPTIONS,
): NormalizeResult {
  const warnings: string[] = [];
  const groups = new Map<string, Array<{ row: Record<string, unknown>; index: number }>>();

  table.rows.forEach((row, index) => {
    const key = mapping.numeroFacture
      ? cleanCell(row[mapping.numeroFacture])
      : `__ligne_${index}`;
    if (!key) {
      warnings.push(`Ligne ${index + 2} du fichier : numero de facture vide, ligne ignoree.`);
      return;
    }
    const bucket = groups.get(key);
    if (bucket) bucket.push({ row, index });
    else groups.set(key, [{ row, index }]);
  });

  const invoices: Invoice[] = [];
  for (const [numero, entries] of groups) {
    invoices.push(buildInvoice(numero, entries, mapping, options, warnings));
  }
  return { invoices, warnings };
}

function get(row: Record<string, unknown>, mapping: ColumnMapping, field: FneField): unknown {
  const column = mapping[field];
  return column ? row[column] : undefined;
}

function text(row: Record<string, unknown>, mapping: ColumnMapping, field: FneField): string {
  return cleanCell(get(row, mapping, field));
}

function buildInvoice(
  numero: string,
  entries: Array<{ row: Record<string, unknown>; index: number }>,
  mapping: ColumnMapping,
  options: NormalizeOptions,
  warnings: string[],
): Invoice {
  const first = entries[0]!.row;
  const sourceRow = entries[0]!.index + 2; // +2 : entete + index base 1

  const date = parseDate(get(first, mapping, "dateFacture"));
  if (!date) {
    warnings.push(`Facture ${numero} : date illisible ou absente (ligne ${sourceRow}).`);
  }

  const lignes = entries.map((entry, position) =>
    buildLine(entry.row, entry.index, position + 1, mapping, options, numero, warnings),
  );

  const kind = detectKind(first, mapping, lignes);

  const totaux = emptyTotals();
  const totalHTSource = parseAmount(get(first, mapping, "totalHT"));
  const totalTvaSource = parseAmount(get(first, mapping, "totalTva"));
  const totalTTCSource = parseAmount(get(first, mapping, "totalTTC"));

  const sommeHT = round(lignes.reduce((sum, line) => sum + line.montantHT, 0), options.decimales);
  const sommeTva = round(lignes.reduce((sum, line) => sum + line.montantTva, 0), options.decimales);
  const sommeTTC = round(lignes.reduce((sum, line) => sum + line.montantTTC, 0), options.decimales);

  totaux.totalHT = totalHTSource ?? sommeHT;
  totaux.totalTva = totalTvaSource ?? sommeTva;
  totaux.totalRemise = parseAmount(get(first, mapping, "totalRemise")) ?? 0;
  totaux.autresTaxes = parseAmount(get(first, mapping, "autresTaxes")) ?? 0;
  totaux.totalTTC = totalTTCSource ?? round(sommeTTC + totaux.autresTaxes, options.decimales);

  return {
    numero,
    numeroFne: text(first, mapping, "numeroFne"),
    codeVerification: text(first, mapping, "codeVerification"),
    date: date ?? "",
    kind,
    client: {
      ...emptyCustomer(),
      code: text(first, mapping, "clientCode"),
      nom: text(first, mapping, "clientNom"),
      ncc: text(first, mapping, "clientNcc"),
      adresse: text(first, mapping, "clientAdresse"),
      telephone: text(first, mapping, "clientTelephone"),
      email: text(first, mapping, "clientEmail"),
    },
    devise: text(first, mapping, "devise") || "XOF",
    reference: text(first, mapping, "reference") || text(first, mapping, "numeroFne"),
    modeReglement: text(first, mapping, "modeReglement"),
    commentaire: "",
    lignes,
    totaux,
  };
}

function buildLine(
  row: Record<string, unknown>,
  rowIndex: number,
  position: number,
  mapping: ColumnMapping,
  options: NormalizeOptions,
  numeroFacture: string,
  warnings: string[],
): InvoiceLine {
  const sourceRow = rowIndex + 2;
  const quantite = parseAmount(get(row, mapping, "quantite")) ?? 1;
  const remisePourcent = parseRate(get(row, mapping, "remisePourcent")) ?? 0;

  const codeTaxeBrut = text(row, mapping, "codeTaxe");
  const tauxSource = parseRate(get(row, mapping, "tauxTva"));
  const taxCode = codeTaxeBrut ? findTaxCode(codeTaxeBrut) : null;
  if (codeTaxeBrut && !taxCode && tauxSource === null) {
    warnings.push(
      `Facture ${numeroFacture}, ligne ${sourceRow} : code taxe "${codeTaxeBrut}" inconnu et aucun taux fourni.`,
    );
  }

  let montantHT = parseAmount(get(row, mapping, "montantHT"));
  let montantTva = parseAmount(get(row, mapping, "montantTva"));
  let montantTTC = parseAmount(get(row, mapping, "montantTTC"));
  let prixUnitaireHT = parseAmount(get(row, mapping, "prixUnitaireHT"));

  if (montantHT === null && prixUnitaireHT !== null) {
    montantHT = round(quantite * prixUnitaireHT * (1 - remisePourcent / 100), options.decimales);
  }
  if (montantHT === null && montantTTC !== null && montantTva !== null) {
    montantHT = round(montantTTC - montantTva, options.decimales);
  }
  montantHT = montantHT ?? 0;

  if (prixUnitaireHT === null) {
    const base = remisePourcent === 100 ? 0 : montantHT / (1 - remisePourcent / 100);
    prixUnitaireHT = quantite !== 0 ? round(base / quantite, 4) : 0;
  }

  let tauxTva = tauxSource ?? taxCode?.taux ?? null;
  if (tauxTva === null && montantTva !== null && montantHT !== 0) {
    tauxTva = round((montantTva / montantHT) * 100, 2);
  }
  if (tauxTva === null) {
    tauxTva = montantTva === 0 ? 0 : options.tauxTvaParDefaut;
  }

  if (montantTva === null) {
    montantTva = round((montantHT * tauxTva) / 100, options.decimales);
  }
  if (montantTTC === null) {
    montantTTC = round(montantHT + montantTva, options.decimales);
  }

  const designation = text(row, mapping, "articleDesignation");
  if (!designation) {
    warnings.push(`Facture ${numeroFacture}, ligne ${sourceRow} : designation d'article absente.`);
  }

  return {
    numero: parseAmount(get(row, mapping, "ligneNumero")) ?? position,
    referenceArticle: text(row, mapping, "articleReference"),
    designation,
    quantite,
    prixUnitaireHT,
    remisePourcent,
    tauxTva,
    codeTaxeFne: taxCode?.code ?? codeTaxeBrut ?? taxCodeFromRate(tauxTva),
    montantHT,
    montantTva,
    montantTTC,
    unite: text(row, mapping, "unite"),
    sourceRow,
  };
}

const AVOIR_KEYWORDS = ["avoir", "refund", "credit", "note de credit", "annulation", "remboursement"];

function detectKind(
  row: Record<string, unknown>,
  mapping: ColumnMapping,
  lignes: InvoiceLine[],
): DocumentKind {
  const raw = normalizeKey(cleanCell(get(row, mapping, "typeDocument")));
  if (raw && AVOIR_KEYWORDS.some((keyword) => raw.includes(normalizeKey(keyword)))) return "AVOIR";
  // Certains exports n'ont pas de colonne type : un avoir se reconnait alors
  // a des montants integralement negatifs.
  if (lignes.length > 0 && lignes.every((line) => line.montantHT < 0)) return "AVOIR";
  return "FACTURE";
}
