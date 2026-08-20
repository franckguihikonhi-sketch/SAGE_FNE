import { Invoice } from "@/lib/core/model";
import { detectMapping, ColumnMapping, missingRequiredFields } from "@/lib/fne/mapping";
import { normalize, DEFAULT_NORMALIZE_OPTIONS, NormalizeOptions } from "@/lib/fne/normalize";
import {
  DEFAULT_NATIVE_OPTIONS,
  FneNativeOptions,
  isFneNativeExport,
  parseFneNative,
} from "@/lib/fne/native";
import { decodeText, readSource, ReadError, SourceTable } from "@/lib/fne/read";
import { PaymentMapping } from "@/lib/fne/paiement";
import { applyCustomerMapping, CustomerMappingEntry, CustomerMappingOptions } from "@/lib/sage/customers";
import { buildSageFile, summarize } from "@/lib/sage/export";
import { findProfile, SAGE100_DOCUMENTS_VENTES, SageImportProfile } from "@/lib/sage/profile";
import {
  DEFAULT_VALIDATION_OPTIONS,
  Issue,
  ValidationOptions,
  validateInvoices,
} from "@/lib/report/validate";
import { FneField } from "@/lib/fne/fields";

export interface ConvertOptions {
  profileId?: string;
  profile?: SageImportProfile;
  /** Mappage manuel qui surcharge la detection automatique des colonnes. */
  mappingOverrides?: ColumnMapping;
  customers?: CustomerMappingEntry[];
  customerOptions?: CustomerMappingOptions;
  /** Correspondance mode de paiement FNE -> code reglement Sage. */
  reglements?: PaymentMapping;
  normalizeOptions?: Partial<NormalizeOptions>;
  validationOptions?: Partial<ValidationOptions>;
  filenameBase?: string;
  /** Feuille Excel a exploiter quand le classeur en contient plusieurs. */
  sheet?: string;
}

export interface ConvertResult {
  source: {
    /** "fne-json" : export natif FNE avec le detail des articles. */
    kind: "fne-json" | "tableau";
    format: string;
    sheet?: string;
    rowCount: number;
    columns: string[];
    /** Vrai quand les lignes ont ete reconstituees depuis les totaux. */
    synthese: boolean;
  };
  mapping: ColumnMapping;
  unmappedColumns: string[];
  /** Colonnes FNE connues mais sans usage cote Sage. */
  ignoredColumns: string[];
  missingFields: FneField[];
  invoices: Invoice[];
  clientsInconnus: Array<{ nom: string; ncc: string; factures: string[] }>;
  issues: Issue[];
  summary: ReturnType<typeof summarize>;
  file: { filename: string; content: string; base64: string; lineCount: number };
  profile: { id: string; label: string };
}

/** Chaine complete : lecture du fichier FNE -> fichier d'import Sage. */
export async function convert(
  buffer: Buffer,
  filename: string,
  options: ConvertOptions = {},
): Promise<ConvertResult> {
  const normalizeOptions: NormalizeOptions = { ...DEFAULT_NORMALIZE_OPTIONS, ...options.normalizeOptions };
  const nativeOptions: FneNativeOptions = {
    ...DEFAULT_NATIVE_OPTIONS,
    numeroPiece: normalizeOptions.numeroPiece,
    avoirEnValeurAbsolue: normalizeOptions.avoirEnValeurAbsolue,
    decimales: normalizeOptions.decimales,
  };

  const native = readNativeExport(buffer, filename);

  let parsed: Invoice[];
  let warnings: string[];
  let mapping: ColumnMapping = {};
  let unmapped: string[] = [];
  let ignored: string[] = [];
  let missing: FneField[] = [];
  let source: ConvertResult["source"];

  if (native) {
    const result = parseFneNative(native, nativeOptions);
    parsed = result.invoices;
    warnings = result.warnings;
    source = {
      kind: "fne-json",
      format: "json",
      rowCount: parsed.length,
      columns: [],
      synthese: parsed.every((invoice) => invoice.lignes.every((line) => !line.referenceArticle)),
    };
  } else {
    const table: SourceTable = await readSource(buffer, filename, options.sheet);
    const detected = detectMapping(table.columns);
    mapping = { ...detected.mapping, ...(options.mappingOverrides ?? {}) };
    unmapped = detected.unmapped;
    ignored = detected.ignored;
    missing = missingRequiredFields(mapping);
    const result = normalize(table, mapping, normalizeOptions);
    parsed = result.invoices;
    warnings = result.warnings;
    source = {
      kind: "tableau",
      format: table.format,
      sheet: table.sheet,
      rowCount: table.rows.length,
      columns: table.columns,
      synthese: result.synthese,
    };
  }

  const { invoices, inconnus } = applyCustomerMapping(parsed, options.customers ?? [], {
    utiliserCodeSource: true,
    ...options.customerOptions,
  });

  const profile =
    options.profile ??
    (options.profileId ? findProfile(options.profileId) : null) ??
    SAGE100_DOCUMENTS_VENTES;

  const validationOptions: ValidationOptions = {
    ...DEFAULT_VALIDATION_OPTIONS,
    synthese: source.synthese,
    ...options.validationOptions,
  };
  const issues: Issue[] = [
    ...warnings.map((message) => ({ severity: "avertissement" as const, code: "LECTURE", message })),
    ...validateInvoices(invoices, validationOptions),
  ];

  const base = options.filenameBase ?? filename.replace(/\.[^.]+$/, "");
  const file = buildSageFile(invoices, profile, `${base}-sage`, options.reglements ?? {});

  return {
    source,
    mapping,
    unmappedColumns: unmapped,
    ignoredColumns: ignored,
    missingFields: missing,
    invoices,
    clientsInconnus: inconnus,
    issues,
    summary: summarize(invoices),
    file: {
      filename: file.filename,
      content: file.preview,
      base64: file.buffer.toString("base64"),
      lineCount: file.lineCount,
    },
    profile: { id: profile.id, label: profile.label },
  };
}

/**
 * Retourne la charge utile JSON quand le fichier est un export natif FNE,
 * `null` sinon (le fichier sera alors traite comme un tableau).
 */
function readNativeExport(buffer: Buffer, filename: string): unknown | null {
  if (!/\.json$/i.test(filename)) return null;
  let payload: unknown;
  try {
    payload = JSON.parse(decodeText(buffer));
  } catch {
    throw new ReadError("Le fichier JSON est illisible.");
  }
  return isFneNativeExport(payload) ? payload : null;
}
