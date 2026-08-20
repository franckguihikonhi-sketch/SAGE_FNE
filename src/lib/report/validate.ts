import { Invoice } from "@/lib/core/model";
import { round } from "@/lib/core/text";
import { isTauxFne, TAUX_FNE } from "@/lib/fne/taxes";
import { SAGE_LIMITS } from "@/lib/sage/limits";

export type Severity = "erreur" | "avertissement";

export interface Issue {
  severity: Severity;
  code: string;
  message: string;
  /** Numero de facture concerne, quand l'anomalie est localisable. */
  facture?: string;
  /** Ligne du fichier source, pour retrouver la donnee d'origine. */
  sourceRow?: number;
}

export interface ValidationOptions {
  /** Tolerance sur l'ecart entre totaux declares et totaux recalcules. */
  toleranceTotaux: number;
  /** Exiger un compte tiers Sage renseigne sur chaque facture. */
  exigerCompteTiers: boolean;
  /** Exiger une reference article sur chaque ligne. */
  exigerReferenceArticle: boolean;
  /**
   * Verifier que chaque taux de TVA appartient a la nomenclature FNE
   * (18 %, 9 %, 0 %). Un taux intermediaire revele une facture a plusieurs
   * taux dont le detail a ete perdu.
   */
  verifierTauxFne: boolean;
  /** Vrai quand les lignes proviennent d'une synthese : adapte le message. */
  synthese: boolean;
}

export const DEFAULT_VALIDATION_OPTIONS: ValidationOptions = {
  toleranceTotaux: 1,
  exigerCompteTiers: true,
  exigerReferenceArticle: false,
  verifierTauxFne: true,
  synthese: false,
};

export function validateInvoices(
  invoices: Invoice[],
  options: ValidationOptions = DEFAULT_VALIDATION_OPTIONS,
): Issue[] {
  const issues: Issue[] = [];
  const seen = new Map<string, number>();

  for (const invoice of invoices) {
    const count = (seen.get(invoice.numero) ?? 0) + 1;
    seen.set(invoice.numero, count);
    if (count === 2) {
      issues.push({
        severity: "erreur",
        code: "PIECE_DUPLIQUEE",
        facture: invoice.numero,
        message: `Le numero de piece ${invoice.numero} apparait plusieurs fois. Sage refusera le doublon.`,
      });
    }

    if (!invoice.date) {
      issues.push({
        severity: "erreur",
        code: "DATE_MANQUANTE",
        facture: invoice.numero,
        message: "Date du document absente ou illisible.",
      });
    }

    if (!invoice.numero) {
      issues.push({
        severity: "erreur",
        code: "PIECE_MANQUANTE",
        message: "Facture sans numero de piece.",
      });
    } else if (invoice.numero.length > SAGE_LIMITS.numeroPiece) {
      issues.push({
        severity: "avertissement",
        code: "PIECE_TROP_LONGUE",
        facture: invoice.numero,
        message:
          `Numero de piece de ${invoice.numero.length} caracteres : Sage en accepte ` +
          `${SAGE_LIMITS.numeroPiece}. Il sera tronque a l'export.`,
      });
    }

    if (options.exigerCompteTiers && !invoice.client.code) {
      issues.push({
        severity: "erreur",
        code: "COMPTE_TIERS_MANQUANT",
        facture: invoice.numero,
        message:
          `Aucun compte tiers Sage pour "${invoice.client.nom || "client inconnu"}"` +
          `${invoice.client.ncc ? ` (NCC ${invoice.client.ncc})` : ""}. ` +
          "Completez la table de correspondance clients.",
      });
    } else if (invoice.client.code.length > SAGE_LIMITS.compteTiers) {
      issues.push({
        severity: "avertissement",
        code: "COMPTE_TIERS_TROP_LONG",
        facture: invoice.numero,
        message: `Compte tiers "${invoice.client.code}" trop long (max ${SAGE_LIMITS.compteTiers}).`,
      });
    }

    if (invoice.lignes.length === 0) {
      issues.push({
        severity: "erreur",
        code: "FACTURE_SANS_LIGNE",
        facture: invoice.numero,
        message: "Facture sans aucune ligne d'article.",
      });
    }

    for (const line of invoice.lignes) {
      if (!line.designation) {
        issues.push({
          severity: "avertissement",
          code: "DESIGNATION_MANQUANTE",
          facture: invoice.numero,
          sourceRow: line.sourceRow,
          message: `Ligne ${line.numero} sans designation.`,
        });
      } else if (line.designation.length > SAGE_LIMITS.designation) {
        issues.push({
          severity: "avertissement",
          code: "DESIGNATION_TRONQUEE",
          facture: invoice.numero,
          sourceRow: line.sourceRow,
          message:
            `Ligne ${line.numero} : designation de ${line.designation.length} caracteres, ` +
            `tronquee a ${SAGE_LIMITS.designation}.`,
        });
      }

      if (options.exigerReferenceArticle && !line.referenceArticle) {
        issues.push({
          severity: "erreur",
          code: "ARTICLE_MANQUANT",
          facture: invoice.numero,
          sourceRow: line.sourceRow,
          message: `Ligne ${line.numero} sans reference article.`,
        });
      }

      if (options.verifierTauxFne && !isTauxFne(line.tauxTva)) {
        issues.push({
          severity: "erreur",
          code: "TAUX_TVA_NON_CONFORME",
          facture: invoice.numero,
          sourceRow: line.sourceRow,
          message: options.synthese
            ? `Taux de TVA reconstitue a ${line.tauxTva} %, hors nomenclature FNE ` +
              `(${TAUX_FNE.join(" / ")} %) : cette facture melange plusieurs taux et l'export ` +
              "tableur n'en porte pas le detail. Utilisez l'export JSON de FNE."
            : `Ligne ${line.numero} : taux de TVA de ${line.tauxTva} %, hors nomenclature FNE ` +
              `(${TAUX_FNE.join(" / ")} %).`,
        });
      }

      if (line.quantite === 0) {
        issues.push({
          severity: "avertissement",
          code: "QUANTITE_NULLE",
          facture: invoice.numero,
          sourceRow: line.sourceRow,
          message: `Ligne ${line.numero} : quantite nulle.`,
        });
      }
    }

    const sommeHT = round(invoice.lignes.reduce((sum, line) => sum + line.montantHT, 0), 2);
    const sommeTva = round(invoice.lignes.reduce((sum, line) => sum + line.montantTva, 0), 2);
    const ecartHT = round(Math.abs(sommeHT - invoice.totaux.totalHT), 2);
    const ecartTva = round(Math.abs(sommeTva - invoice.totaux.totalTva), 2);

    if (ecartHT > options.toleranceTotaux) {
      issues.push({
        severity: "avertissement",
        code: "ECART_TOTAL_HT",
        facture: invoice.numero,
        message:
          `Total HT declare ${invoice.totaux.totalHT} contre ${sommeHT} recalcule ` +
          `depuis les lignes (ecart ${ecartHT}).`,
      });
    }
    if (ecartTva > options.toleranceTotaux) {
      issues.push({
        severity: "avertissement",
        code: "ECART_TOTAL_TVA",
        facture: invoice.numero,
        message:
          `Total TVA declare ${invoice.totaux.totalTva} contre ${sommeTva} recalcule ` +
          `depuis les lignes (ecart ${ecartTva}).`,
      });
    }
  }

  return issues;
}

export function countBySeverity(issues: Issue[]) {
  return {
    erreurs: issues.filter((issue) => issue.severity === "erreur").length,
    avertissements: issues.filter((issue) => issue.severity === "avertissement").length,
  };
}
