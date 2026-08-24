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
import { findTaxCode, taxCodeFromRate, TAUX_FNE } from "./taxes";
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
  /**
   * Article de synthese par taux de TVA, pour les exports sans detail.
   *
   * Les taux listes sont ceux que l'entreprise pratique : ils determinent en
   * quoi une facture a taux melange peut etre decomposee. Le format d'import
   * du dossier ne transportant pas la taxe, c'est la fiche article qui donne
   * son regime a chaque ligne - il faut donc un article par taux, sans quoi
   * Sage appliquerait le meme regime a toutes les parts.
   *
   * Vide : les trois taux de la nomenclature FNE (18, 9 et 0), sans article.
   */
  articlesSynthese: Array<{ taux: number; article: string }>;
}

export const DEFAULT_NORMALIZE_OPTIONS: NormalizeOptions = {
  tauxTvaParDefaut: 18,
  decimales: 2,
  // L'exemplaire reel du client laisse la zone du numero de piece vide :
  // Sage numerote lui-meme. C'est donc le defaut, le seul comportement
  // dont on sait qu'il est accepte a l'import.
  numeroPiece: "vide",
  avoirEnValeurAbsolue: true,
  libelleSynthese: "Facture FNE {reference}",
  articlesSynthese: [],
};

/**
 * Facture dont le detail a ete reconstitue en une part taxable et une part
 * exoneree. Presentee en tableau plutot qu'en avertissements repetes : quatorze
 * fois la meme phrase n'aide personne a verifier quoi que ce soit.
 */
export interface Reconstitution {
  reference: string;
  tauxEffectif: number;
  /** Les deux parts reconstituees, du taux le plus eleve au plus bas. */
  parts: Array<{ taux: number; ht: number; tva: number; article: string }>;
  /** Part du total HT portee par le taux le plus bas, pour reperer les cas atypiques. */
  partBasse: number;
}

export interface NormalizeResult {
  invoices: Invoice[];
  /** Anomalies non bloquantes rencontrees pendant la normalisation. */
  warnings: string[];
  /** Vrai quand les lignes ont ete reconstituees depuis les totaux. */
  synthese: boolean;
  /** Factures partagees entre deux taux de TVA. */
  reconstitutions: Reconstitution[];
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
      "L'export ne contient pas le detail des articles : les lignes ont ete reconstituees a " +
        "partir des totaux de chaque facture. Utilisez l'export JSON de FNE pour obtenir le " +
        "detail reel des articles.",
    );
  }

  type Entree = { row: Record<string, unknown>; index: number };
  // Cle de regroupement d'un cote, reference affichee de l'autre : sans colonne
  // de numero, chaque ligne est une facture a elle seule et ne porte aucune
  // reference. La cle technique ne doit alors jamais ressortir dans le fichier
  // d'import ni dans les messages.
  const groups = new Map<string, { reference: string; entries: Entree[] }>();
  table.rows.forEach((row, index) => {
    const reference = mapping.numeroFacture ? cleanCell(row[mapping.numeroFacture]) : "";
    if (mapping.numeroFacture && !reference) {
      warnings.push(`Ligne ${index + 2} du fichier : numero de facture vide, ligne ignoree.`);
      return;
    }
    const key = mapping.numeroFacture ? reference : `ligne:${index}`;
    const bucket = groups.get(key);
    if (bucket) bucket.entries.push({ row, index });
    else groups.set(key, { reference, entries: [{ row, index }] });
  });

  const invoices: Invoice[] = [];
  const contexte: { reconstitutions: Reconstitution[] } = { reconstitutions: [] };
  for (const { reference, entries } of groups.values()) {
    invoices.push(buildInvoice(reference, entries, mapping, options, synthese, warnings, contexte));
  }

  return { invoices, warnings, synthese, reconstitutions: contexte.reconstitutions };
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
  contexte: { reconstitutions: Reconstitution[] },
): Invoice {
  const first = entries[0]!.row;
  const sourceRow = entries[0]!.index + 2; // +2 : ligne d'entete + index base 1
  // De quoi designer la piece dans un message quand elle n'a pas de reference.
  const piece = reference || `ligne ${sourceRow}`;

  const date = parseDate(get(first, mapping, "dateFacture"));
  if (!date) warnings.push(`Facture ${piece} : date illisible ou absente (ligne ${sourceRow}).`);

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
    ? syntheseLines(
        reference,
        piece,
        totalHTSource ?? 0,
        totalTvaSource ?? 0,
        options,
        sourceRow,
        warnings,
        contexte,
      )
    : entries.map((entry, position) =>
        buildLine(entry.row, entry.index, position + 1, mapping, options, signe, piece, warnings),
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

/**
 * Paliers de taux entre lesquels une facture peut etre decomposee : les taux
 * pratiques par l'entreprise, plus l'exoneration, qui est toujours possible.
 */
function paliersDeTaux(articles: NormalizeOptions["articlesSynthese"]): number[] {
  const taux = articles.length > 0 ? articles.map((entree) => entree.taux) : TAUX_FNE;
  return [...new Set([0, ...taux])].filter((valeur) => valeur >= 0).sort((a, b) => a - b);
}

/**
 * Reconstitution des lignes d'une facture dont l'export ne porte pas le detail.
 *
 * Le taux effectif (total TVA / total HT) tranche entre deux cas.
 *
 * S'il correspond a un taux pratique par l'entreprise, la facture ne porte
 * qu'un seul taux : une ligne suffit, portee par l'article de ce taux.
 *
 * Sinon la facture melange deux taux, et le taux effectif dit lesquels : ce
 * sont les deux paliers qui l'encadrent. Une entreprise qui facture a 18 % et
 * a 9 % produit ainsi des taux effectifs entre les deux, et un melange de
 * taxable et d'exonere donne un taux effectif sous le taux le plus bas. La
 * repartition entre les deux paliers est alors exacte :
 *
 *     HT(haut) = (100 x TVA - bas x HT) / (haut - bas)
 *
 * La facture est reconstituee en deux lignes, aux taux reels, plutot que
 * d'etre rejetee pour un taux moyen qui n'existe pas.
 *
 * Ce partage se verifie : sur l'export reel du client, les quatorze factures
 * a taux intermediaire donnent, au palier 18 %, un nombre entier d'unites du
 * prix unitaire certifie - ce que l'hypothese 18 % / exonere ne donnait jamais.
 */
function syntheseLines(
  reference: string,
  /** Designation de la piece dans les messages, quand elle n'a pas de reference. */
  piece: string,
  totalHT: number,
  totalTva: number,
  options: NormalizeOptions,
  sourceRow: number,
  warnings: string[],
  contexte: { reconstitutions: Reconstitution[] },
): InvoiceLine[] {
  const d = options.decimales;
  const tauxEffectif = totalHT !== 0 ? round((totalTva / totalHT) * 100, 2) : 0;
  // Un libelle sans reference ne doit pas laisser trainer un separateur seul :
  // "Facture FNE " ecrit tel quel dans Sage n'aiderait personne.
  const libelle = options.libelleSynthese.replace("{reference}", reference).trim() || "Facture FNE";

  const ligne = (
    numero: number,
    article: string,
    designation: string,
    montantHT: number,
    tauxTva: number,
    montantTva: number,
  ): InvoiceLine => ({
    numero,
    referenceArticle: article,
    designation,
    quantite: 1,
    prixUnitaireHT: montantHT,
    remisePourcent: 0,
    tauxTva,
    codeTaxeFne: taxCodeFromRate(tauxTva),
    montantHT,
    montantTva,
    montantTTC: round(montantHT + montantTva, d),
    unite: "",
    sourceRow,
  });

  const paliers = paliersDeTaux(options.articlesSynthese);
  const article = (taux: number) =>
    options.articlesSynthese.find((entree) => Math.abs(entree.taux - taux) <= 0.01)?.article ?? "";

  const exact = paliers.find((palier) => Math.abs(palier - tauxEffectif) <= 0.01);
  if (exact !== undefined) {
    return [ligne(1, article(exact), libelle, totalHT, exact, totalTva)];
  }

  const haut = paliers.find((palier) => palier > tauxEffectif);
  const bas = [...paliers].reverse().find((palier) => palier < tauxEffectif);
  if (haut === undefined || bas === undefined || totalHT === 0) {
    // Taux effectif hors de tout encadrement : une seule ligne, et la
    // validation signalera un taux non conforme.
    return [ligne(1, article(tauxEffectif), libelle, totalHT, tauxEffectif, totalTva)];
  }

  // Le partage se calcule sur les totaux, jamais sur le taux effectif arrondi :
  // sur un ecart de neuf points, un centieme de point deplace des dizaines de
  // francs, et le montant reconstitue cesserait de tomber juste.
  const htHaut = round((100 * totalTva - bas * totalHT) / (haut - bas), d);
  const htBas = round(totalHT - htHaut, d);
  const tvaHaut = round((htHaut * haut) / 100, d);
  // Le solde plutot qu'un second calcul : le total TVA de la facture est
  // conserve au centime, quel que soit l'arrondi de la premiere part.
  const tvaBas = round(totalTva - tvaHaut, d);

  const parts = [
    { taux: haut, ht: htHaut, tva: tvaHaut, article: article(haut) },
    { taux: bas, ht: htBas, tva: tvaBas, article: article(bas) },
  ];

  contexte.reconstitutions.push({
    reference: piece,
    tauxEffectif,
    parts,
    partBasse: round((htBas / totalHT) * 100, 1),
  });

  return parts.map((part, index) =>
    ligne(
      index + 1,
      part.article,
      `${libelle} - part a ${part.taux} %`,
      part.ht,
      part.taux,
      part.tva,
    ),
  );
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
