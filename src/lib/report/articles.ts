import { Invoice } from "@/lib/core/model";
import { Issue } from "./validate";

/**
 * Synthese par article des taux de TVA rencontres dans l'export FNE.
 *
 * Quand le format d'import Sage ne transporte pas la taxe, c'est le regime de
 * TVA de la fiche article qui s'applique. Cette synthese sert a verifier que
 * le parametrage Sage des articles correspond a ce que FNE a certifie.
 */
export interface ArticleResume {
  reference: string;
  designation: string;
  /** Taux rencontres dans l'export, du plus frequent au moins frequent. */
  taux: number[];
  /** Codes taxe FNE rencontres (TVA, TVAB, TVAC, TVAD). */
  codesTaxe: string[];
  unite: string;
  /** Nombre de lignes de facture portant cet article. */
  lignes: number;
}

export function resumeArticles(invoices: Invoice[]): ArticleResume[] {
  const index = new Map<
    string,
    { resume: ArticleResume; taux: Map<number, number>; codes: Set<string> }
  >();

  for (const invoice of invoices) {
    for (const line of invoice.lignes) {
      const cle = line.referenceArticle || line.designation;
      if (!cle) continue;
      let entry = index.get(cle);
      if (!entry) {
        entry = {
          resume: {
            reference: line.referenceArticle,
            designation: line.designation,
            taux: [],
            codesTaxe: [],
            unite: line.unite,
            lignes: 0,
          },
          taux: new Map(),
          codes: new Set(),
        };
        index.set(cle, entry);
      }
      entry.resume.lignes += 1;
      entry.taux.set(line.tauxTva, (entry.taux.get(line.tauxTva) ?? 0) + 1);
      if (line.codeTaxeFne) entry.codes.add(line.codeTaxeFne);
    }
  }

  return [...index.values()]
    .map(({ resume, taux, codes }) => ({
      ...resume,
      taux: [...taux.entries()].sort((a, b) => b[1] - a[1]).map(([valeur]) => valeur),
      codesTaxe: [...codes].sort(),
    }))
    .sort((a, b) => b.lignes - a.lignes);
}

/**
 * Un article vu avec plusieurs taux dans le meme export ne peut pas etre repris
 * par un format sans zone de taxe : la fiche article ne porte qu'un regime.
 */
export function controleArticles(articles: ArticleResume[]): Issue[] {
  return articles
    .filter((article) => article.taux.length > 1)
    .map((article) => ({
      severity: "erreur" as const,
      code: "ARTICLE_MULTI_TAUX",
      message:
        `L'article ${article.reference || article.designation} apparait avec plusieurs taux de TVA ` +
        `(${article.taux.join(" / ")} %). La fiche article Sage ne portant qu'un seul regime, ce cas ` +
        "impose d'ajouter une zone de taxe au format d'import.",
    }));
}
