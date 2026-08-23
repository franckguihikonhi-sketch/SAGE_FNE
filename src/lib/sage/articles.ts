import { Invoice } from "@/lib/core/model";
import { normalizeKey } from "@/lib/core/text";

/**
 * Correspondance entre les references d'article de FNE et celles du dossier Sage.
 *
 * Les deux nomenclatures n'ont aucune raison de coincider : le fichier
 * d'import de reference du client porte des codes numeriques (1147005,
 * 1149001) la ou FNE certifie des references alphanumeriques (6FF001).
 * Importer la reference FNE telle quelle ferait rejeter la ligne par Sage, ou
 * creerait un article inconnu.
 */
export interface ArticleMappingEntry {
  /** Reference telle qu'elle figure dans l'export FNE. */
  referenceFne: string;
  /** Reference de l'article dans le dossier Sage (AR_Ref). */
  referenceSage: string;
}

export interface ArticleMappingOptions {
  /** Article utilise quand aucune correspondance n'existe. */
  articleParDefaut?: string;
  /**
   * Si vrai, la reference FNE est conservee quand aucune correspondance n'est
   * trouvee. C'est le comportement adapte aux dossiers dont les articles
   * portent deja la reference FNE.
   */
  conserverReferenceFne?: boolean;
}

export interface ArticleMappingResult {
  invoices: Invoice[];
  /** Articles sans correspondance : a creer dans Sage ou a ajouter a la table. */
  inconnus: Array<{ referenceFne: string; designation: string; lignes: number }>;
}

export function applyArticleMapping(
  invoices: Invoice[],
  entries: ArticleMappingEntry[],
  options: ArticleMappingOptions = {},
): ArticleMappingResult {
  const table = new Map<string, string>();
  for (const entry of entries) {
    if (entry.referenceFne) table.set(normalizeKey(entry.referenceFne), entry.referenceSage);
  }

  const inconnus = new Map<string, { referenceFne: string; designation: string; lignes: number }>();

  const mapped = invoices.map((invoice) => ({
    ...invoice,
    lignes: invoice.lignes.map((ligne) => {
      const source = ligne.referenceArticle;
      if (!source) return ligne;

      const correspondance = table.get(normalizeKey(source));
      if (correspondance) return { ...ligne, referenceArticle: correspondance };

      const existant = inconnus.get(normalizeKey(source));
      if (existant) existant.lignes += 1;
      else {
        inconnus.set(normalizeKey(source), {
          referenceFne: source,
          designation: ligne.designation,
          lignes: 1,
        });
      }

      if (options.conserverReferenceFne) return ligne;
      return { ...ligne, referenceArticle: options.articleParDefaut ?? source };
    }),
  }));

  return { invoices: mapped, inconnus: [...inconnus.values()] };
}

/** Lit une table de correspondance : `referenceFne;referenceSage`, une par ligne. */
export function parseArticleMappingCsv(text: string): ArticleMappingEntry[] {
  const entries: ArticleMappingEntry[] = [];
  for (const [index, ligne] of text.split(/\r?\n/).entries()) {
    if (ligne.trim() === "") continue;
    const cellules = ligne.split(/[;,\t]/).map((cell) => cell.trim().replace(/^"|"$/g, ""));
    // Ligne d'entete eventuelle.
    if (index === 0 && /reference|article|sage|fne/i.test(ligne) && cellules.length >= 2) {
      const [premier = ""] = cellules;
      if (/reference|article|fne/i.test(premier)) continue;
    }
    const [referenceFne = "", referenceSage = ""] = cellules;
    if (!referenceFne || !referenceSage) continue;
    entries.push({ referenceFne, referenceSage });
  }
  return entries;
}

/**
 * Article de synthese associe a un taux de TVA.
 *
 * Un export sans detail des articles ne permet que des lignes de synthese, et
 * le format d'import du dossier ne transporte pas la taxe : c'est la fiche
 * article Sage qui donne son regime a la ligne. Il faut donc un article par
 * taux pratique par l'entreprise - a defaut, toutes les parts d'une facture a
 * taux melange recevraient le meme regime.
 */
export interface ArticleTaux {
  taux: number;
  article: string;
}

/**
 * Lit une table `taux=article`, une par ligne : `18=DIVERS18`. Le point-virgule
 * et la tabulation sont admis comme separateurs, le signe % est ignore.
 */
export function parseArticlesTauxText(text: string): ArticleTaux[] {
  const entries: ArticleTaux[] = [];
  const vus = new Set<number>();

  for (const ligne of text.split(/\r?\n/)) {
    if (ligne.trim() === "" || ligne.trim().startsWith("#")) continue;
    const [brut = "", ...reste] = ligne.split(/[=;,\t]/);
    const taux = Number(brut.replace("%", "").replace(",", ".").trim());
    const article = reste.join("").trim();
    if (!Number.isFinite(taux) || taux < 0 || taux > 100) continue;
    if (vus.has(taux)) continue;
    vus.add(taux);
    entries.push({ taux, article });
  }

  return entries.sort((a, b) => b.taux - a.taux);
}
