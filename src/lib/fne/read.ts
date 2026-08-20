import Papa from "papaparse";
import { cleanCell } from "@/lib/core/text";
import { decodeText } from "@/lib/core/cp1252";
import { ReadError, type SourceTable } from "./source";

export { decodeText };
export { ReadError, type SourceTable } from "./source";



export function readCsv(buffer: Buffer): SourceTable {
  const text = decodeText(buffer);
  const parsed = Papa.parse<Record<string, unknown>>(text, {
    header: true,
    skipEmptyLines: "greedy",
    delimiter: "", // auto-detection (';' pour les exports FR, ',' ou tabulation sinon)
    transformHeader: (header) => cleanCell(header),
  });

  const columns = (parsed.meta.fields ?? []).filter((field) => field !== "");
  if (columns.length === 0) throw new ReadError("Aucune colonne detectee dans le fichier CSV.");

  return { columns, rows: parsed.data, format: "csv" };
}

export async function readXlsx(buffer: Buffer, sheetName?: string): Promise<SourceTable> {
  const ExcelJS = (await import("exceljs")).default;
  const workbook = new ExcelJS.Workbook();
  await workbook.xlsx.load(buffer as unknown as ArrayBuffer);

  const worksheet = sheetName
    ? workbook.getWorksheet(sheetName)
    : workbook.worksheets.find((sheet) => sheet.rowCount > 1) ?? workbook.worksheets[0];
  if (!worksheet) throw new ReadError("Le classeur Excel ne contient aucune feuille exploitable.");

  const headerRowIndex = findHeaderRow(worksheet);
  const headerRow = worksheet.getRow(headerRowIndex);
  const columns: string[] = [];
  headerRow.eachCell({ includeEmpty: true }, (cell, colNumber) => {
    const label = cleanCell(cellValue(cell));
    columns[colNumber - 1] = label || `Colonne ${colNumber}`;
  });
  const usedColumns = columns.map((label, index) => label ?? `Colonne ${index + 1}`);

  const rows: Array<Record<string, unknown>> = [];
  worksheet.eachRow({ includeEmpty: false }, (row, rowNumber) => {
    if (rowNumber <= headerRowIndex) return;
    const record: Record<string, unknown> = {};
    let hasValue = false;
    usedColumns.forEach((label, index) => {
      const value = cellValue(row.getCell(index + 1));
      record[label] = value;
      if (cleanCell(value) !== "") hasValue = true;
    });
    if (hasValue) rows.push(record);
  });

  return { columns: usedColumns, rows, format: "xlsx", sheet: worksheet.name };
}

/**
 * Les exports FNE peuvent commencer par des lignes de titre ou de logo.
 * La ligne d'entete est la premiere ligne comportant au moins deux libelles
 * textuels distincts.
 */
function findHeaderRow(worksheet: import("exceljs").Worksheet): number {
  const limit = Math.min(worksheet.rowCount, 20);
  for (let rowNumber = 1; rowNumber <= limit; rowNumber += 1) {
    const row = worksheet.getRow(rowNumber);
    const labels = new Set<string>();
    row.eachCell({ includeEmpty: false }, (cell) => {
      const text = cleanCell(cellValue(cell));
      if (text && Number.isNaN(Number(text))) labels.add(text.toLowerCase());
    });
    if (labels.size >= 2) return rowNumber;
  }
  return 1;
}

function cellValue(cell: import("exceljs").Cell): unknown {
  const value = cell.value;
  if (value === null || value === undefined) return "";
  if (typeof value === "object") {
    if (value instanceof Date) return value;
    if ("text" in value && typeof value.text === "string") return value.text;
    if ("result" in value) return (value as { result?: unknown }).result ?? "";
    if ("richText" in value) {
      return (value as { richText: Array<{ text: string }> }).richText.map((part) => part.text).join("");
    }
    if ("hyperlink" in value) return (value as { text?: string }).text ?? "";
  }
  return value;
}

/**
 * Lecture d'un export JSON (reponse de l'API FNE ou export applicatif).
 * On aplatit les objets imbriques : `client.ncc` devient la colonne "client.ncc".
 */
export function readJson(buffer: Buffer): SourceTable {
  let payload: unknown;
  try {
    payload = JSON.parse(decodeText(buffer));
  } catch {
    throw new ReadError("Le fichier JSON est illisible.");
  }

  const records = extractRecords(payload);
  if (records.length === 0) throw new ReadError("Aucun enregistrement trouve dans le fichier JSON.");

  const flattened = records.map((record) => flatten(record));
  const columns: string[] = [];
  for (const row of flattened) {
    for (const key of Object.keys(row)) {
      if (!columns.includes(key)) columns.push(key);
    }
  }
  return { columns, rows: flattened, format: "json" };
}

function extractRecords(payload: unknown): Array<Record<string, unknown>> {
  if (Array.isArray(payload)) return payload as Array<Record<string, unknown>>;
  if (payload && typeof payload === "object") {
    const container = payload as Record<string, unknown>;
    for (const key of ["data", "items", "invoices", "factures", "results", "content"]) {
      const candidate = container[key];
      if (Array.isArray(candidate)) return candidate as Array<Record<string, unknown>>;
    }
    return [container];
  }
  return [];
}

function flatten(value: unknown, prefix = ""): Record<string, unknown> {
  const output: Record<string, unknown> = {};
  if (value === null || typeof value !== "object") {
    if (prefix) output[prefix] = value;
    return output;
  }
  if (Array.isArray(value)) {
    // Les tableaux (typiquement les lignes de facture) sont conserves tels quels :
    // c'est `normalize` qui decide comment les eclater.
    if (prefix) output[prefix] = value;
    return output;
  }
  for (const [key, child] of Object.entries(value as Record<string, unknown>)) {
    const path = prefix ? `${prefix}.${key}` : key;
    if (child && typeof child === "object" && !Array.isArray(child) && !(child instanceof Date)) {
      Object.assign(output, flatten(child, path));
    } else {
      output[path] = child;
    }
  }
  return output;
}

export async function readSource(buffer: Buffer, filename: string, sheet?: string): Promise<SourceTable> {
  const extension = filename.toLowerCase().split(".").pop() ?? "";
  switch (extension) {
    case "csv":
    case "txt":
      return readCsv(buffer);
    case "xlsx":
    case "xlsm":
      return readXlsx(buffer, sheet);
    case "json":
      return readJson(buffer);
    case "xls":
      throw new ReadError(
        "Le format .xls (Excel 97-2003) n'est pas pris en charge. Enregistrez le fichier en .xlsx ou en .csv depuis Excel.",
      );
    default:
      throw new ReadError(`Extension de fichier non reconnue : .${extension}`);
  }
}
