import { Invoice } from "@/lib/core/model";
import { detectMapping, ColumnMapping, missingRequiredFields } from "@/lib/fne/mapping";
import { normalize, DEFAULT_NORMALIZE_OPTIONS, NormalizeOptions } from "@/lib/fne/normalize";
import { readSource, SourceTable } from "@/lib/fne/read";
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
  normalizeOptions?: Partial<NormalizeOptions>;
  validationOptions?: Partial<ValidationOptions>;
  filenameBase?: string;
  /** Feuille Excel a exploiter quand le classeur en contient plusieurs. */
  sheet?: string;
}

export interface ConvertResult {
  table: Pick<SourceTable, "columns" | "format" | "sheet"> & { rowCount: number };
  mapping: ColumnMapping;
  unmappedColumns: string[];
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
  const table = await readSource(buffer, filename, options.sheet);
  const detected = detectMapping(table.columns);
  const mapping: ColumnMapping = { ...detected.mapping, ...(options.mappingOverrides ?? {}) };

  const normalizeOptions: NormalizeOptions = { ...DEFAULT_NORMALIZE_OPTIONS, ...options.normalizeOptions };
  const { invoices: parsed, warnings } = normalize(table, mapping, normalizeOptions);

  const { invoices, inconnus } = applyCustomerMapping(
    parsed,
    options.customers ?? [],
    { utiliserCodeSource: true, ...options.customerOptions },
  );

  const profile =
    options.profile ??
    (options.profileId ? findProfile(options.profileId) : null) ??
    SAGE100_DOCUMENTS_VENTES;

  const validationOptions: ValidationOptions = {
    ...DEFAULT_VALIDATION_OPTIONS,
    ...options.validationOptions,
  };
  const issues: Issue[] = [
    ...warnings.map((message) => ({ severity: "avertissement" as const, code: "LECTURE", message })),
    ...validateInvoices(invoices, validationOptions),
  ];

  const base = options.filenameBase ?? filename.replace(/\.[^.]+$/, "");
  const file = buildSageFile(invoices, profile, `${base}-sage`);

  return {
    table: {
      columns: table.columns,
      format: table.format,
      sheet: table.sheet,
      rowCount: table.rows.length,
    },
    mapping,
    unmappedColumns: detected.unmapped,
    missingFields: missingRequiredFields(mapping),
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
