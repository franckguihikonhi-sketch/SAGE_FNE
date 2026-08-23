import { Invoice } from "@/lib/core/model";
import { toBase64 } from "@/lib/core/cp1252";
import { detectMapping, ColumnMapping, missingRequiredFields } from "@/lib/fne/mapping";
import {
  normalize,
  DEFAULT_NORMALIZE_OPTIONS,
  NormalizeOptions,
  type Reconstitution,
} from "@/lib/fne/normalize";
import {
  DEFAULT_NATIVE_OPTIONS,
  FneNativeOptions,
  isFneNativeExport,
  parseFneNative,
} from "@/lib/fne/native";
import { ReadError, type SourceTable } from "@/lib/fne/source";
import { ColonnesOptions, restreindreColonnes } from "@/lib/fne/colonnes";
import { decodeText } from "@/lib/core/cp1252";
import { PaymentMapping } from "@/lib/fne/paiement";
import { applyCustomerMapping, CustomerMappingEntry, CustomerMappingOptions } from "@/lib/sage/customers";
import { applyArticleMapping, ArticleMappingEntry, ArticleMappingOptions } from "@/lib/sage/articles";
import { buildSageFile, summarize } from "@/lib/sage/export";
import { PARAMETRES_CONNUS } from "@/lib/sage/tokens";
import {
  findProfile,
  porteLaTaxe,
  SAGE100_IMPORT_EXPORT,
  SageImportProfile,
} from "@/lib/sage/profile";
import {
  DEFAULT_VALIDATION_OPTIONS,
  Issue,
  ValidationOptions,
  validateInvoices,
} from "@/lib/report/validate";
import { FneField } from "@/lib/fne/fields";
import { ArticleResume, controleArticles, resumeArticles } from "@/lib/report/articles";

export interface ConvertOptions {
  profileId?: string;
  profile?: SageImportProfile;
  /** Mappage manuel qui surcharge la detection automatique des colonnes. */
  mappingOverrides?: ColumnMapping;
  customers?: CustomerMappingEntry[];
  customerOptions?: CustomerMappingOptions;
  /** Correspondance reference article FNE -> reference article Sage. */
  articles?: ArticleMappingEntry[];
  articleOptions?: ArticleMappingOptions;
  /** Correspondance mode de paiement FNE -> code reglement Sage. */
  reglements?: PaymentMapping;
  /** Valeurs propres au dossier Sage : depot, souche... (jetons `parametre.<nom>`). */
  parametres?: Record<string, string>;
  normalizeOptions?: Partial<NormalizeOptions>;
  validationOptions?: Partial<ValidationOptions>;
  filenameBase?: string;
  /** Feuille Excel a exploiter quand le classeur en contient plusieurs. */
  sheet?: string;
  /** Colonnes de l'export tableur retenues, par leur lettre. Voir `colonnes.ts`. */
  colonnes?: Partial<ColonnesOptions>;
  /**
   * Lecteur de tableaux (CSV / Excel). Injecte par l'appelant : le serveur passe
   * le lecteur Node, le navigateur le sien. Le pipeline reste ainsi utilisable
   * des deux cotes, sans embarquer de dependance Node dans le bundle web.
   */
  reader?: (buffer: Uint8Array, filename: string, sheet?: string) => Promise<SourceTable>;
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
    /** Lettres des colonnes retenues, quand une restriction s'applique. */
    colonnesRetenues?: string[];
    /** Libelles des colonnes ecartees par la restriction. */
    colonnesEcartees?: string[];
  };
  mapping: ColumnMapping;
  unmappedColumns: string[];
  /** Colonnes FNE connues mais sans usage cote Sage. */
  ignoredColumns: string[];
  missingFields: FneField[];
  invoices: Invoice[];
  /** Synthese des taux de TVA par article, pour verifier le parametrage Sage. */
  articles: ArticleResume[];
  /** Factures partagees entre part taxable et part exoneree, a verifier. */
  reconstitutions: Reconstitution[];
  clientsInconnus: Array<{ nom: string; ncc: string; factures: string[] }>;
  /** Articles FNE sans equivalent dans le dossier Sage. */
  articlesInconnus: Array<{ referenceFne: string; designation: string; lignes: number }>;
  issues: Issue[];
  summary: ReturnType<typeof summarize>;
  file: { filename: string; content: string; base64: string; lineCount: number };
  profile: { id: string; label: string };
}

/** Chaine complete : lecture du fichier FNE -> fichier d'import Sage. */
export async function convert(
  buffer: Uint8Array,
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
  let reconstitutions: Reconstitution[] = [];
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
    if (!options.reader) {
      throw new ReadError(
        "Ce format demande un lecteur de tableaux. Utilisez convertFichier (serveur) " +
          "ou convertNavigateur (web).",
      );
    }
    const lu: SourceTable = await options.reader(buffer, filename, options.sheet);
    // L'export tableur FNE porte trente-trois colonnes dont neuf servent a
    // l'import : la restriction est appliquee avant la detection des champs,
    // qui ne voit donc que les colonnes retenues.
    const restriction = restreindreColonnes(lu, options.colonnes);
    const table = restriction.table;
    const detected = detectMapping(table.columns);
    mapping = { ...detected.mapping, ...(options.mappingOverrides ?? {}) };
    unmapped = detected.unmapped;
    ignored = detected.ignored;
    missing = missingRequiredFields(mapping);
    const result = normalize(table, mapping, normalizeOptions);
    parsed = result.invoices;
    warnings = [...restriction.avertissements, ...result.warnings];
    reconstitutions = result.reconstitutions;
    source = {
      kind: "tableau",
      format: table.format,
      sheet: table.sheet,
      rowCount: table.rows.length,
      columns: table.columns,
      synthese: result.synthese,
      colonnesRetenues: restriction.retenues,
      colonnesEcartees: restriction.ecartees,
    };
  }

  const clients = applyCustomerMapping(parsed, options.customers ?? [], {
    utiliserCodeSource: true,
    ...options.customerOptions,
  });
  // Les references d'article de FNE et de Sage n'ont aucune raison de coincider.
  const articlesMappes = applyArticleMapping(clients.invoices, options.articles ?? [], {
    conserverReferenceFne: true,
    ...options.articleOptions,
  });
  const invoices = articlesMappes.invoices;
  const inconnus = clients.inconnus;

  const profile =
    options.profile ??
    (options.profileId ? findProfile(options.profileId) : null) ??
    SAGE100_IMPORT_EXPORT;

  const validationOptions: ValidationOptions = {
    ...DEFAULT_VALIDATION_OPTIONS,
    synthese: source.synthese,
    numerotationSage: normalizeOptions.numeroPiece === "vide",
    ...options.validationOptions,
  };
  // En mode synthese, chaque "article" est une facture reconstituee : la
  // synthese par article n'aurait aucun sens.
  const articles = source.synthese ? [] : resumeArticles(invoices);
  const taxeDansLeFormat = porteLaTaxe(profile);
  const issues: Issue[] = [
    ...warnings.map((message) => ({ severity: "avertissement" as const, code: "LECTURE", message })),
    ...validateInvoices(invoices, validationOptions),
    ...controleTaxe(invoices, profile),
    // Le controle des articles vaut quel que soit le format : une reference qui
    // est en realite un compte tiers fait echouer l'import dans tous les cas.
    ...controleArticles(articles, taxeDansLeFormat),
  ];

  const base = options.filenameBase ?? filename.replace(/\.[^.]+$/, "");
  const file = buildSageFile(invoices, profile, `${base}-sage`, {
    reglements: options.reglements ?? {},
    parametres: { ...defaultParametres(), ...(options.parametres ?? {}) },
  });

  return {
    source,
    mapping,
    unmappedColumns: unmapped,
    ignoredColumns: ignored,
    missingFields: missing,
    invoices,
    articles,
    reconstitutions,
    clientsInconnus: inconnus,
    articlesInconnus: articlesMappes.inconnus,
    issues,
    summary: summarize(invoices),
    file: {
      filename: file.filename,
      content: file.preview,
      base64: toBase64(file.buffer),
      lineCount: file.lineCount,
    },
    profile: { id: profile.id, label: profile.label },
  };
}

/**
 * Le format d'import peut ne comporter aucune zone de taxe : Sage applique
 * alors le regime de TVA de la fiche article. Ce n'est pas une anomalie en soi
 * - c'est meme le fonctionnement normal quand chaque article a un regime fixe -
 * mais cela demande que le parametrage Sage corresponde a ce que FNE certifie.
 * Le cas reellement bloquant, un meme article vu a plusieurs taux, est traite
 * par `controleArticles`.
 */
function controleTaxe(invoices: Invoice[], profile: SageImportProfile): Issue[] {
  if (porteLaTaxe(profile)) return [];
  const taux = [...new Set(invoices.flatMap((invoice) => invoice.lignes.map((line) => line.tauxTva)))];
  if (taux.length === 0) return [];

  const rencontres = taux.sort((a, b) => b - a);
  const tauxNormalSeul = rencontres.length === 1 && rencontres[0] === 18;

  // Un fichier entierement au taux normal ne demande aucune action tant que
  // les articles Sage sont eux-memes au taux normal. Des qu'un autre taux
  // apparait, la fiche article devient decisive et l'ecart possible.
  const consequence = tauxNormalSeul
    ? "Toutes les lignes sont au taux normal : l'import sera juste si les articles Sage " +
      "correspondants le sont aussi. Rien d'autre a faire."
    : `Taux rencontres : ${rencontres.join(" / ")} %. Les articles exoneres ou a taux reduit ` +
      "doivent porter ce regime dans leur fiche Sage, faute de quoi la TVA importee sera fausse. " +
      "Verifiez la synthese par article.";

  return [
    {
      severity: "avertissement",
      code: "TAXE_ABSENTE_DU_FORMAT",
      message:
        `Le format "${profile.label}" ne comporte aucune zone de taxe : c'est le regime de TVA de ` +
        `la fiche article Sage qui s'appliquera. ${consequence}`,
    },
  ];
}

function defaultParametres(): Record<string, string> {
  return Object.fromEntries(PARAMETRES_CONNUS.map((entry) => [entry.nom, entry.defaut]));
}

/**
 * Retourne la charge utile JSON quand le fichier est un export natif FNE,
 * `null` sinon (le fichier sera alors traite comme un tableau).
 */
function readNativeExport(buffer: Uint8Array, filename: string): unknown | null {
  if (!/\.json$/i.test(filename)) return null;
  let payload: unknown;
  try {
    payload = JSON.parse(decodeText(buffer));
  } catch {
    throw new ReadError("Le fichier JSON est illisible.");
  }
  return isFneNativeExport(payload) ? payload : null;
}
