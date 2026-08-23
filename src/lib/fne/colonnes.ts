import { normalizeKey } from "@/lib/core/text";
import type { SourceTable } from "./source";

/**
 * Restriction de l'export tableur FNE aux colonnes utiles.
 *
 * L'export tableur de FNE compte trente-trois colonnes ; neuf d'entre elles
 * portent ce qu'un document de vente Sage demande : la date, les totaux, le
 * client et le vendeur. Les autres - terminal, RCCM, regime d'imposition,
 * pied de page, horodatages - n'ont aucun usage comptable, et un libelle qui
 * ressemble a un alias peut detourner la detection automatique des champs.
 *
 * La restriction se fait par *position*, comme le cabinet designe ses
 * colonnes, et ne s'applique qu'a un fichier reconnu comme un export tableur
 * FNE : appliquer les memes lettres a un CSV quelconque n'aurait pas de sens.
 */

/** Colonnes retenues : F, I, J, K, L, N, O, P, U de l'export tableur FNE. */
export const COLONNES_RETENUES_DEFAUT = "F;I;J;K;L;N;O;P;U";

/**
 * Colonnes qui ne portent aucun montant mais sans lesquelles un document perd
 * son identite : A la reference de la facture d'origine (le lien d'un avoir
 * vers ce qu'il annule), C la reference certifiee FNE, E le sous-type
 * (`normal` / `refund`) qui distingue une facture d'un avoir, G le mode de
 * paiement d'ou se deduit le code reglement Sage.
 */
export const COLONNES_COMPLEMENT = "A;C;E;G";

export interface ColonnesOptions {
  /**
   * Lettres des colonnes retenues, separees par `;`, `,` ou un espace.
   * Vide : toutes les colonnes du fichier sont exploitees.
   */
  retenues: string;
  /**
   * Ajoute les colonnes de `COLONNES_COMPLEMENT`. Sans elles, les factures
   * n'ont plus de reference FNE, les avoirs ne sont plus rattaches a leur
   * facture d'origine et le mode de reglement est perdu : le defaut est donc
   * de les conserver.
   */
  complement: boolean;
}

export const DEFAULT_COLONNES_OPTIONS: ColonnesOptions = {
  retenues: COLONNES_RETENUES_DEFAUT,
  complement: true,
};

/** "A" -> 0, "C" -> 2, "AA" -> 26. Retourne -1 si ce n'est pas une lettre de colonne. */
export function indexDeLettre(lettre: string): number {
  const propre = lettre.trim().toUpperCase();
  if (!/^[A-Z]+$/.test(propre)) return -1;
  let index = 0;
  for (const caractere of propre) index = index * 26 + (caractere.charCodeAt(0) - 64);
  return index - 1;
}

/** 0 -> "A", 26 -> "AA". */
export function lettreDeIndex(index: number): string {
  let reste = index + 1;
  let lettre = "";
  while (reste > 0) {
    const modulo = (reste - 1) % 26;
    lettre = String.fromCharCode(65 + modulo) + lettre;
    reste = Math.floor((reste - 1) / 26);
  }
  return lettre;
}

/**
 * Lit une specification de colonnes. Tolerante a la forme dictee a l'oral :
 * `"F; I; J; K; L; N; O; P et U"` comme `"F,I,J"`.
 */
export function analyserColonnes(specification: string): number[] {
  const positions = specification
    .split(/[^A-Za-z]+/)
    .filter((mot) => mot !== "" && mot.toLowerCase() !== "et")
    .map((mot) => indexDeLettre(mot))
    .filter((position) => position >= 0);
  return [...new Set(positions)].sort((a, b) => a - b);
}

/**
 * Signature de l'export tableur FNE : des libelles connus a des positions
 * connues. Cinq concordances sur sept suffisent - FNE peut renommer une
 * colonne sans changer sa place.
 */
const SIGNATURE: Array<[string, string]> = [
  ["C", "reference"],
  ["E", "sous type de facture"],
  ["F", "date"],
  ["I", "total ht"],
  ["L", "total ttc"],
  ["O", "ncc du client"],
  ["U", "nom du vendeur"],
];

export function estExportTableurFne(columns: string[]): boolean {
  const concordances = SIGNATURE.filter(([lettre, libelle]) => {
    const colonne = columns[indexDeLettre(lettre)];
    return colonne !== undefined && normalizeKey(colonne) === normalizeKey(libelle);
  });
  return concordances.length >= 5;
}

export interface RestrictionColonnes {
  /** Le tableau ramene aux colonnes retenues, ou le tableau d'origine. */
  table: SourceTable;
  /** Lettres effectivement retenues, dans l'ordre du fichier. */
  retenues: string[];
  /** Libelles des colonnes ecartees. */
  ecartees: string[];
  avertissements: string[];
}

/**
 * Ramene un tableau aux colonnes retenues. Les lignes conservent leurs cles :
 * seules les colonnes ecartees disparaissent, si bien que la detection des
 * champs et la normalisation ne voient plus que ce qui a ete demande.
 */
export function restreindreColonnes(
  table: SourceTable,
  options: Partial<ColonnesOptions> = {},
): RestrictionColonnes {
  const { retenues: specification, complement } = { ...DEFAULT_COLONNES_OPTIONS, ...options };
  const intacte: RestrictionColonnes = { table, retenues: [], ecartees: [], avertissements: [] };
  if (specification.trim() === "") return intacte;

  const demandees = analyserColonnes(specification);
  if (demandees.length === 0) return intacte;

  // Les lettres designent des positions de l'export tableur FNE : les
  // appliquer a un autre fichier ecarterait des colonnes au hasard.
  if (!estExportTableurFne(table.columns)) return intacte;

  const positions = new Set(demandees);
  if (complement) for (const position of analyserColonnes(COLONNES_COMPLEMENT)) positions.add(position);

  const gardees = table.columns.filter((_, index) => positions.has(index));
  if (gardees.length === 0) return intacte;

  const ecartees = table.columns.filter((_, index) => !positions.has(index));
  const avertissements: string[] = [];
  if (ecartees.length > 0) {
    avertissements.push(
      `Lecture restreinte aux colonnes ${lettresLisibles([...positions].sort((a, b) => a - b))} : ` +
        `${ecartees.length} colonne${ecartees.length > 1 ? "s" : ""} du fichier ` +
        `${ecartees.length > 1 ? "ont ete ecartees" : "a ete ecartee"}.`,
    );
  }
  if (!complement) avertissements.push(...structurePerdue(table, positions));

  const rows = table.rows.map((row) => {
    const reduite: Record<string, unknown> = {};
    for (const colonne of gardees) reduite[colonne] = row[colonne];
    return reduite;
  });

  return {
    table: { ...table, columns: gardees, rows },
    retenues: table.columns.map((_, index) => index).filter((index) => positions.has(index)).map(lettreDeIndex),
    ecartees,
    avertissements,
  };
}

function lettresLisibles(positions: number[]): string {
  return positions.map(lettreDeIndex).join(", ");
}

/**
 * Mesure ce que coute l'abandon des colonnes de structure, pendant qu'elles
 * sont encore lisibles. Un avoir reste reconnaissable au signe negatif de ses
 * totaux : ce qui se perd vraiment, ce sont les avoirs a totaux positifs, et
 * eux seuls seraient comptabilises a l'envers. Annoncer leur nombre exact vaut
 * mieux qu'un avertissement general.
 */
function structurePerdue(table: SourceTable, retenues: Set<number>): string[] {
  const messages = [
    "Les colonnes A, C, E et G ne sont pas retenues : les factures sont importees sans " +
      "reference FNE, les avoirs sans lien vers la facture d'origine, et sans mode de reglement. " +
      "Le controle des doublons perd son seul point d'appui.",
  ];

  const colonne = table.columns[indexDeLettre("E")];
  if (!colonne) return messages;
  const avoirs = table.rows.filter((row) => /refund|avoir|credit/i.test(String(row[colonne] ?? "")));
  if (avoirs.length === 0) return messages;

  // Colonne de total encore lisible apres restriction, dans l'ordre de fiabilite.
  const position = analyserColonnes("L;N;I").find((index) => retenues.has(index));
  const colonneTotal = position === undefined ? undefined : table.columns[position];
  const positifs = colonneTotal
    ? avoirs.filter((row) => !`${row[colonneTotal] ?? ""}`.trim().startsWith("-")).length
    : avoirs.length;

  messages.push(
    positifs === 0
      ? `Les ${avoirs.length} avoirs de ce fichier restent reconnus au signe negatif de leurs ` +
        "totaux. Un avoir dont les totaux seraient positifs serait importe comme une facture."
      : `${positifs} avoir${positifs > 1 ? "s" : ""} de ce fichier ` +
        `${positifs > 1 ? "seront importes" : "sera importe"} comme facture : leurs totaux sont ` +
        "positifs et la colonne E, qui les distingue, n'est pas retenue.",
  );
  return messages;
}
