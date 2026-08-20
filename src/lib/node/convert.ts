import { convert, ConvertOptions, ConvertResult } from "@/lib/pipeline";
import { readSource } from "@/lib/fne/read";

/**
 * Point d'entree serveur : le pipeline avec le lecteur Node (CSV via papaparse,
 * Excel via exceljs). Le pipeline lui-meme ne depend d'aucun module Node, ce qui
 * permet de le rejouer tel quel dans le navigateur.
 */
export function convertFichier(
  buffer: Buffer,
  filename: string,
  options: ConvertOptions = {},
): Promise<ConvertResult> {
  return convert(buffer, filename, {
    reader: (bytes, nom, feuille) => readSource(Buffer.from(bytes), nom, feuille),
    ...options,
  });
}

export type { ConvertOptions, ConvertResult };
