import { parseDate } from "@/lib/core/date";
import {
  DocumentKind,
  Invoice,
  InvoiceLine,
  emptyCustomer,
  emptyInvoice,
} from "@/lib/core/model";
import { cleanCell, normalizeKey, parseAmount, parseRate, round } from "@/lib/core/text";
import { ColumnMapping } from "./mapping";
import { FneField, LINE_AMOUNT_FIELDS } from "./fields";
import { findTaxCode, taxCodeFromRate } from "./taxes";
import { numeroPiece } from "./native";
import type { SourceTable } from "./source";

export interface NormalizeOptions {
  /**
   * Taux de TVA applique quand ni le code taxe ni le taux ne sont exploitables
   * et que la ligne porte une TVA non nulle.
   */
  tauxTvaParDefaut: number;
  /** Nombre de decimales des montants. */
  decimales: number;
  /** Voir `FneNativeOptions.numeroPiece`. */
  numeroPiece: "sequence" | "reference" | "vide";
  /** Voir `FneNativeOptions.avoirEnValeurAbsolue`. */
  avoirEnValeurAbsolue: boolean;
  /**
   * Libelle de la ligne generee quand l'export ne porte pas le detail des
   * articles (cas de l'export tableur FNE, qui n'exporte que les entetes).
   */
  libelleSynthese: string;
  /** Reference article utilisee par la ligne de synthese. */
  articleSynthese: string;
}

export const DEFAULT_NORMALIZE_OPTIONS: NormalizeOptions = {
  tauxTvaParDefaut: 18,
  decimales: 2,
  numeroPiece: "sequence",
  avoirEnValeurAbsolue: true,
  libelleSynthese: "Facture FNE {reference}",
  articleSynthese: "",
};

export interface NormalizeResult {
  invoices: Invoice[];
  /** Anomalies non bloquantes rencontrees pendant la normalisation. */
  warnings: string[];
  /** Vrai quand les lignes ont ete reconstituees depuis les totaux. */
  synthese: boolean;
}

export function normalize(
  table: SourceTable,
  mapping: ColumnMapping,
  options: NormalizeOptions = DEFAULT_NORMALIZE_OPTIONS,
): NormalizeResult {
  const warnings: string[] = [];
  // L'export tableur FNE ne contient que les entetes de facture : sans aucune
  // zone de detail, une ligne de synthese est reconstituee depuis les totaux.
  const synthese = LINE_AMOUNT_FIELDS.every((field) => !mapping[field]);
  if (synthese) {
    warnings.push(
      "L'export ne contient pas le detail des articles : une ligne de synthese a ete generee " +
        "par facture a partir des totaux. Utilisez l'export JSON de FNE pour obtenir le detail.",
    );
  }

  const groups = new Map<string, Array<{ row: Record<string, unknown>; index: number }>>();
  table.rows.forEach((row, index) => {
    const key = mapping.numeroFacture ? cleanCell(row[mapping.numeroFacture]) : `__ligne_${index}`;
    if (!key) {
      warnings.push(`Ligne ${index + 2} du fichier : numero de facture vide, ligne ignoree.`);
      return;
    }
    const bucket = groups.get(key);
    if (bucket) bucket.push({ row, index });
    else groups.set(key, [{ row, index }]);
  });

  const invoices: Invoice[] = [];
  for (const [reference, entries] of groups) {
    invoices.push(buildInvoice(reference, entries, mapping, options, synthese, warnings));
  }
  return { invoices, warnings, synthese };
}

function get(row: Record<string, unknown>, mapping: ColumnMapping, field: FneField): unknown {
  const column = mapping[field];
  return column ? row[column] : undefined;
}

function text(row: Record<string, unknown>, mapping: ColumnMapping, field: FneField): string {
  return cleanCell(get(row, mapping, field));
}

function amount(
  row: Record<string, unknown>,
  mapping: ColumnMapping,
  field: FneField,
  signe: number,
  decimales: number,
): number | null {
  const value = parseAmount(get(row, mapping, field));
  return value === null ? null : round(signe * value, decimales);
}

function buildInvoice(
  reference: string,
  entries: Array<{ row: Record<string, unknown>; index: number }>,
  mapping: ColumnMapping,
  options: NormalizeOptions,
  synthese: boolean,
  warnings: string[],
): Invoice {
  const first = entries[0]!.row;
  const sourceRow = entries[0]!.index + 2; // +2 : ligne d'entete + index base 1

  const date = parseDate(get(first, mapping, "dateFacture"));
  if (!date) warnings.push(`Facture ${reference} : date illisible ou absente (ligne ${sourceRow}).`);

  const kind = detectKind(first, mapping, entries, options);
  const signe = kind === "AVOIR" && options.avoirEnValeurAbsolue ? -1 : 1;
  const d = options.decimales;

  const invoice = emptyInvoice();
  invoice.numero = numeroPiece(reference, options.numeroPiece);
  invoice.numeroFne = text(first, mapping, "numeroFne") || reference;
  invoice.numeroParent = text(first, mapping, "referenceParent");
  invoice.codeVerification = text(first, mapping, "codeVerification");
  invoice.date = date ?? "";
  invoice.kind = kind;
  invoice.devise = text(first, mapping, "devise") || "XOF";
  invoice.reference = text(first, mapping, "reference") || invoice.numeroParent || invoice.numeroFne;
  invoice.modeReglement = text(first, mapping, "modeReglement");
  invoice.template = text(first, mapping, "template");
  invoice.vendeur = text(first, mapping, "vendeur");
  invoice.pointDeVente = text(first, mapping, "pointDeVente");
  invoice.etablissement = text(first, mapping, "etablissement");
  invoice.client = {
    ...emptyCustomer(),
    code: text(first, mapping, "clientCode"),
    nom: text(first, mapping, "clientNom"),
    ncc: text(first, mapping, "clientNcc"),
    adresse: text(first, mapping, "clientAdresse"),
    telephone: text(first, mapping, "clientTelephone"),
    email: text(first, mapping, "clientEmail"),
  };

  const totalHTSource = amount(first, mapping, "totalHT", signe, d);
  const totalTvaSource = amount(first, mapping, "totalTva", signe, d);
  const totalTTCSource = amount(first, mapping, "totalTTC", signe, d);

  invoice.lignes = synthese
    ? [syntheseLine(reference, totalHTSource ?? 0, totalTvaSource ?? 0, options, sourceRow)]
    : entries.map((entry, position) =>
        buildLine(entry.row, entry.index, position + 1, mapping, options, signe, reference, warnings),
      );

  const sommeHT = round(invoice.lignes.reduce((sum, line) => sum + line.montantHT, 0), d);
  const sommeTva = round(invoice.lignes.reduce((sum, line) => sum + line.montantTva, 0), d);
  const sommeTTC = round(invoice.lignes.reduce((sum, line) => sum + line.montantTTC, 0), d);

  invoice.totaux = {
    totalHT: totalHTSource ?? sommeHT,
    totalRemise: amount(first, mapping, "totalRemise", signe, d) ?? 0,
    totalTva: totalTvaSource ?? sommeTva,
    autresTaxes: amount(first, mapping, "autresTaxes", signe, d) ?? 0,
    timbre: amount(first, mapping, "timbre", signe, d) ?? 0,
    totalTTC: totalTTCSource ?? sommeTTC,
    netAPayer: 0,
  };
  invoice.totaux.netAPayer =
    amount(first, mapping, "netAPayer", signe, d) ??
    round(invoice.totaux.totalTTC + invoice.totaux.timbre, d);

  return invoice;
}

function buildLine(
  row: Record<string, unknown>,
  rowIndex: number,
  position: number,
  mapping: ColumnMapping,
  options: NormalizeOptions,
  signe: number,
  reference: string,
  warnings: string[],
): InvoiceLine {
  const sourceRow = rowIndex + 2;
  const d = options.decimales;
  const quantite = parseAmount(get(row, mapping, "quantite")) ?? 1;
  const remisePourcent = parseRate(get(row, mapping, "remisePourcent")) ?? 0;

  const codeTaxeBrut = text(row, mapping, "codeTaxe");
  const tauxSource = parseRate(get(row, mapping, "tauxTva"));
  const taxCode = codeTaxeBrut ? findTaxCode(codeTaxeBrut) : null;
  if (codeTaxeBrut && !taxCode && tauxSource === null) {
    warnings.push(
      `Facture ${reference}, ligne ${sourceRow} : code taxe "${codeTaxeBrut}" inconnu et aucun taux fourni.`,
    );
  }

  let montantHT = amount(row, mapping, "montantHT", signe, d);
  let montantTva = amount(row, mapping, "montantTva", signe, d);
  let montantTTC = amount(row, mapping, "montantTTC", signe, d);
  let prixUnitaireHT = amount(row, mapping, "prixUnitaireHT", signe, 6);

  if (montantHT === null && prixUnitaireHT !== null) {
    montantHT = round(quantite * prixUnitaireHT * (1 - remisePourcent / 100), d);
  }
  if (montantHT === null && montantTTC !== null && montantTva !== null) {
    montantHT = round(montantTTC - montantTva, d);
  }
  montantHT = montantHT ?? 0;

  if (prixUnitaireHT === null) {
    const base = remisePourcent === 100 ? 0 : montantHT / (1 - remisePourcent / 100);
    prixUnitaireHT = quantite !== 0 ? round(base / quantite, 6) : 0;
  }

  let tauxTva = tauxSource ?? taxCode?.taux ?? null;
  if (tauxTva === null && montantTva !== null && montantHT !== 0) {
    tauxTva = round((montantTva / montantHT) * 100, 2);
  }
  if (tauxTva === null) tauxTva = montantTva === 0 ? 0 : options.tauxTvaParDefaut;

  if (montantTva === null) montantTva = round((montantHT * tauxTva) / 100, d);
  if (montantTTC === null) montantTTC = round(montantHT + montantTva, d);

  const designation = text(row, mapping, "articleDesignation");
  if (!designation) {
    warnings.push(`Facture ${reference}, ligne ${sourceRow} : designation d'article absente.`);
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

function syntheseLine(
  reference: string,
  totalHT: number,
  totalTva: number,
  options: NormalizeOptions,
  sourceRow: number,
): InvoiceLine {
  const tauxTva = totalHT !== 0 ? round((totalTva / totalHT) * 100, 2) : 0;
  return {
    numero: 1,
    referenceArticle: options.articleSynthese,
    designation: options.libelleSynthese.replace("{reference}", reference),
    quantite: 1,
    prixUnitaireHT: totalHT,
    remisePourcent: 0,
    tauxTva,
    codeTaxeFne: taxCodeFromRate(tauxTva),
    montantHT: totalHT,
    montantTva: totalTva,
    montantTTC: round(totalHT + totalTva, options.decimales),
    unite: "",
    sourceRow,
  };
}

const AVOIR_KEYWORDS = ["avoir", "refund", "credit", "annulation", "remboursement"];

function detectKind(
  row: Record<string, unknown>,
  mapping: ColumnMapping,
  entries: Array<{ row: Record<string, unknown>; index: number }>,
  options: NormalizeOptions,
): DocumentKind {
  // L'export tableur FNE porte la nature dans "Sous-type de facture" (normal / refund).
  for (const field of ["sousTypeDocument", "typeDocument"] as const) {
    const raw = normalizeKey(cleanCell(get(row, mapping, field)));
    if (raw && AVOIR_KEYWORDS.some((keyword) => raw.includes(keyword))) return "AVOIR";
  }
  if (mapping.referenceParent && cleanCell(get(row, mapping, "referenceParent"))) return "AVOIR";

  // A defaut de colonne de nature, un avoir se reconnait a des montants negatifs.
  const totalField: FneField = mapping.totalTTC ? "totalTTC" : "montantHT";
  const total = parseAmount(get(row, mapping, totalField));
  if (total !== null && total < 0) return "AVOIR";
  if (!options.avoirEnValeurAbsolue) return "FACTURE";
  const tousNegatifs = entries.every((entry) => {
    const value = parseAmount(get(entry.row, mapping, "montantHT"));
    return value !== null && value < 0;
  });
  return tousNegatifs && entries.length > 0 ? "AVOIR" : "FACTURE";
}
