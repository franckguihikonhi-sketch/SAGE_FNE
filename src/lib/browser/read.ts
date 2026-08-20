import { decodeText } from "@/lib/core/cp1252";
import { cleanCell } from "@/lib/core/text";
import { ReadError, type SourceTable } from "@/lib/fne/source";
import { listEntries, readEntry } from "./zip";

/**
 * Lecteur de tableaux cote navigateur.
 *
 * Meme role que `src/lib/fne/read.ts` cote serveur, mais sans papaparse ni
 * exceljs : la page publiee doit rester autonome et legere.
 */
export async function readSourceBrowser(
  bytes: Uint8Array,
  filename: string,
  sheet?: string,
): Promise<SourceTable> {
  const extension = filename.toLowerCase().split(".").pop() ?? "";
  if (extension === "csv" || extension === "txt") return readCsv(bytes);
  if (extension === "xlsx" || extension === "xlsm") return readXlsx(bytes, sheet);
  if (extension === "json") return readJson(bytes);
  throw new ReadError(`Format non gere : .${extension}. Utilisez un fichier JSON, Excel ou CSV.`);
}

/** Detecte le separateur sur la ligne d'entete : point-virgule, tabulation ou virgule. */
function detectDelimiter(header: string): string {
  const candidats = [";", "\t", ","];
  return candidats.reduce((meilleur, candidat) =>
    header.split(candidat).length > header.split(meilleur).length ? candidat : meilleur,
  );
}

/** Decoupe une ligne CSV en respectant les guillemets et les doublements ("" -> "). */
function splitLine(line: string, delimiter: string): string[] {
  const cells: string[] = [];
  let current = "";
  let quoted = false;

  for (let i = 0; i < line.length; i += 1) {
    const char = line[i]!;
    if (quoted) {
      if (char === '"') {
        if (line[i + 1] === '"') {
          current += '"';
          i += 1;
        } else quoted = false;
      } else current += char;
      continue;
    }
    if (char === '"') quoted = true;
    else if (char === delimiter) {
      cells.push(current);
      current = "";
    } else current += char;
  }
  cells.push(current);
  return cells;
}

function readCsv(bytes: Uint8Array): SourceTable {
  const text = decodeText(bytes).replace(/\r\n/g, "\n");
  const lines = text.split("\n").filter((line) => line.trim() !== "");
  if (lines.length === 0) throw new ReadError("Fichier vide.");

  const delimiter = detectDelimiter(lines[0]!);
  const columns = splitLine(lines[0]!, delimiter).map((cell) => cleanCell(cell));
  if (columns.length === 0) throw new ReadError("Aucune colonne detectee dans le fichier CSV.");

  const rows = lines.slice(1).map((line) => {
    const cells = splitLine(line, delimiter);
    return Object.fromEntries(columns.map((column, index) => [column, cells[index] ?? ""]));
  });
  return { columns, rows, format: "csv" };
}

function readJson(bytes: Uint8Array): SourceTable {
  let payload: unknown;
  try {
    payload = JSON.parse(decodeText(bytes));
  } catch {
    throw new ReadError("Le fichier JSON est illisible.");
  }
  const rows = Array.isArray(payload) ? payload : [payload];
  const columns = [...new Set(rows.flatMap((row) => Object.keys(row as object)))];
  return { columns, rows: rows as Array<Record<string, unknown>>, format: "json" };
}

/** Convertit une reference de cellule Excel (B12) en index de colonne (1). */
function columnIndex(reference: string): number {
  const lettres = reference.replace(/[0-9]/g, "");
  let index = 0;
  for (const lettre of lettres) index = index * 26 + (lettre.charCodeAt(0) - 64);
  return index - 1;
}

function decodeXmlEntities(value: string): string {
  return value
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&#(\d+);/g, (_, code: string) => String.fromCharCode(Number(code)))
    .replace(/&amp;/g, "&");
}

/** Concatene le texte des balises <t> d'un fragment XML. */
function textOf(xml: string): string {
  const parts = [...xml.matchAll(/<t[^>]*>([\s\S]*?)<\/t>/g)].map((match) => match[1] ?? "");
  return decodeXmlEntities(parts.join(""));
}

async function readXlsx(bytes: Uint8Array, sheetName?: string): Promise<SourceTable> {
  const entries = listEntries(bytes);

  const partages = entries.get("xl/sharedStrings.xml");
  const chaines = partages
    ? [...(await readEntry(bytes, partages)).matchAll(/<si>([\s\S]*?)<\/si>/g)].map((match) =>
        textOf(match[1] ?? ""),
      )
    : [];

  // Le classeur donne l'ordre et les noms des feuilles ; les fichiers suivent cet ordre.
  const classeur = entries.get("xl/workbook.xml");
  const noms = classeur
    ? [...(await readEntry(bytes, classeur)).matchAll(/<sheet[^>]*name="([^"]*)"/g)].map((match) =>
        decodeXmlEntities(match[1] ?? ""),
      )
    : [];

  const position = sheetName ? Math.max(0, noms.indexOf(sheetName)) : 0;
  const feuille = entries.get(`xl/worksheets/sheet${position + 1}.xml`);
  if (!feuille) throw new ReadError("Aucune feuille exploitable dans le classeur.");

  const xml = await readEntry(bytes, feuille);
  const lignes: string[][] = [];

  for (const ligne of xml.matchAll(/<row[^>]*>([\s\S]*?)<\/row>/g)) {
    const cellules: string[] = [];
    for (const cellule of (ligne[1] ?? "").matchAll(/<c([^>]*)>([\s\S]*?)<\/c>/g)) {
      const attributs = cellule[1] ?? "";
      const contenu = cellule[2] ?? "";
      const reference = attributs.match(/r="([A-Z]+)\d+"/)?.[1];
      const type = attributs.match(/t="([^"]*)"/)?.[1];

      let valeur: string;
      if (type === "s") {
        const index = Number(contenu.match(/<v>(\d+)<\/v>/)?.[1] ?? "-1");
        valeur = chaines[index] ?? "";
      } else if (type === "inlineStr") {
        valeur = textOf(contenu);
      } else {
        valeur = decodeXmlEntities(contenu.match(/<v>([\s\S]*?)<\/v>/)?.[1] ?? "");
      }

      const index = reference ? columnIndex(reference) : cellules.length;
      while (cellules.length < index) cellules.push("");
      cellules[index] = valeur;
    }
    lignes.push(cellules);
  }

  const entete = lignes.shift();
  if (!entete) throw new ReadError("Feuille vide.");
  const columns = entete.map((cell) => cleanCell(cell));

  const rows = lignes
    .filter((cellules) => cellules.some((cell) => cell !== ""))
    .map((cellules) =>
      Object.fromEntries(columns.map((column, index) => [column, cellules[index] ?? ""])),
    );

  return { columns, rows, format: "xlsx", sheet: noms[position] };
}
