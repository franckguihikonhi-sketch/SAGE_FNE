import iconv from "iconv-lite";
import { Invoice, InvoiceLine } from "@/lib/core/model";
import { SageColumn, SageImportProfile } from "./profile";
import { resolveToken, TokenContext } from "./tokens";

export interface SageExportResult {
  /** Contenu encode, pret a etre ecrit sur disque ou telecharge. */
  buffer: Buffer;
  /** Meme contenu en texte, pour l'apercu dans l'interface. */
  preview: string;
  filename: string;
  lineCount: number;
}

export function buildSageFile(
  invoices: Invoice[],
  profile: SageImportProfile,
  filenameBase = "import-sage",
): SageExportResult {
  const rows: string[] = [];

  if (profile.includeHeaderRow) {
    const layout = profile.entete.length > 0 ? profile.entete : profile.ligne;
    rows.push(joinRow(layout.map((column) => column.label), profile, layout));
  }

  for (const invoice of invoices) {
    if (profile.entete.length > 0) {
      rows.push(renderRow(profile.entete, { invoice, line: null, profile }, profile));
    }
    for (const line of invoice.lignes) {
      rows.push(renderRow(profile.ligne, { invoice, line, profile }, profile));
    }
  }

  const content = rows.join(profile.eol) + (rows.length > 0 ? profile.eol : "");
  const buffer =
    profile.encoding === "windows-1252"
      ? iconv.encode(content, "windows-1252")
      : Buffer.from(content, "utf8");

  return {
    buffer,
    preview: content,
    filename: `${filenameBase}.${profile.extension}`,
    lineCount: rows.length,
  };
}

function renderRow(columns: SageColumn[], ctx: TokenContext, profile: SageImportProfile): string {
  const values = columns.map((column) => {
    switch (column.source.kind) {
      case "const":
        return column.source.value;
      case "empty":
        return "";
      case "token":
        return resolveToken(column.source.token, { ...ctx, profile: withDecimals(profile, column) });
    }
  });
  return joinRow(values, profile, columns);
}

/**
 * Une colonne peut surcharger le nombre de decimales du profil
 * (ex. quantite a 3 decimales, montants a 2).
 */
function withDecimals(profile: SageImportProfile, column: SageColumn): SageImportProfile {
  if (column.decimals === undefined) return profile;
  return { ...profile, decimals: column.decimals };
}

function joinRow(values: string[], profile: SageImportProfile, columns: SageColumn[]): string {
  const cells = values.map((value, index) => formatCell(value, profile, columns[index]));
  if (profile.layout === "fixed") return cells.join("");
  return cells.join(profile.delimiter);
}

function formatCell(value: string, profile: SageImportProfile, column?: SageColumn): string {
  let cell = sanitize(value, profile);

  if (profile.layout === "fixed") {
    const length = column?.length ?? cell.length;
    const pad = column?.pad ?? " ";
    cell = cell.length > length ? cell.slice(0, length) : cell;
    return column?.align === "right" ? cell.padStart(length, pad) : cell.padEnd(length, pad);
  }

  if (column?.length !== undefined && cell.length > column.length) {
    cell = cell.slice(0, column.length);
  }

  if (profile.quote) {
    const needsQuote = cell.includes(profile.delimiter) || cell.includes(profile.quote);
    if (needsQuote) {
      return profile.quote + cell.split(profile.quote).join(profile.quote + profile.quote) + profile.quote;
    }
  }
  return cell;
}

/**
 * Retire les caracteres qui casseraient le fichier d'import :
 * sauts de ligne, tabulations parasites et separateur de zones quand
 * le profil n'encadre pas les valeurs.
 */
function sanitize(value: string, profile: SageImportProfile): string {
  let cell = value.replace(/[\r\n]+/g, " ");
  if (!profile.quote && profile.layout === "delimited") {
    cell = cell.split(profile.delimiter).join(" ");
  }
  return cell.replace(/\t/g, " ").trim();
}

/** Somme de controle affichee a l'utilisateur avant import dans Sage. */
export function summarize(invoices: Invoice[]) {
  const lignes = invoices.reduce((sum, invoice) => sum + invoice.lignes.length, 0);
  const totalTTC = invoices.reduce((sum, invoice) => sum + invoice.totaux.totalTTC, 0);
  const totalHT = invoices.reduce((sum, invoice) => sum + invoice.totaux.totalHT, 0);
  const totalTva = invoices.reduce((sum, invoice) => sum + invoice.totaux.totalTva, 0);
  const avoirs = invoices.filter((invoice) => invoice.kind === "AVOIR").length;
  return { factures: invoices.length - avoirs, avoirs, lignes, totalHT, totalTva, totalTTC };
}

export type { InvoiceLine };
