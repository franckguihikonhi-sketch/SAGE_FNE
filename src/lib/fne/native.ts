import { parseDate } from "@/lib/core/date";
import { DocumentKind, Invoice, InvoiceLine, emptyInvoice } from "@/lib/core/model";
import { cleanCell, round } from "@/lib/core/text";
import { findTaxCode, taxCodeFromRate } from "./taxes";

/**
 * Lecture de l'export JSON natif de la plateforme FNE.
 *
 * C'est le seul export qui porte le detail des articles (`items`) : l'export
 * tableur ne contient que les entetes de facture. La structure suit la reponse
 * de l'API de certification decrite par la DGI.
 */

export interface FneNativeOptions {
  /**
   * Numero de piece transmis a Sage :
   * - "sequence" : partie annee + numero de la reference FNE (ex. 2304903U26000000889 -> 26000000889),
   *   compatible avec la longueur du champ DO_Piece de Sage ;
   * - "reference" : reference FNE complete, a n'utiliser que si le champ Sage a ete etendu ;
   * - "vide" : zone laissee vide pour que Sage numerote lui-meme le document.
   */
  numeroPiece: "sequence" | "reference" | "vide";
  /**
   * Les avoirs FNE portent des montants negatifs. Sage exprime l'avoir par le
   * type de document et attend des montants positifs : true retablit le signe.
   */
  avoirEnValeurAbsolue: boolean;
  /** Nombre de decimales des montants de ligne et des totaux. */
  decimales: number;
}

export const DEFAULT_NATIVE_OPTIONS: FneNativeOptions = {
  numeroPiece: "vide",
  avoirEnValeurAbsolue: true,
  decimales: 2,
};

interface FneTax {
  amount?: number;
  name?: string;
  shortName?: string;
}

interface FneItem {
  quantity?: number;
  reference?: string;
  description?: string;
  amount?: number;
  discount?: number | null;
  measurementUnit?: string | null;
  taxes?: FneTax[];
  customTaxes?: FneTax[];
}

interface FneInvoice {
  reference?: string;
  parentReference?: string | null;
  token?: string;
  type?: string;
  subtype?: string;
  date?: string;
  paymentMethod?: string;
  template?: string;
  amount?: number;
  vatAmount?: number;
  fiscalStamp?: number | null;
  discount?: number | null;
  totalBeforeTaxes?: number;
  totalDiscounted?: number;
  totalTaxes?: number;
  totalAfterTaxes?: number;
  totalCustomTaxes?: number;
  totalDue?: number;
  clientNcc?: string | null;
  clientCompanyName?: string | null;
  clientPhone?: string | null;
  clientEmail?: string | null;
  clientSellerName?: string | null;
  clientEstablishment?: string | null;
  clientPointOfSale?: string | null;
  foreignCurrency?: string | null;
  description?: string | null;
  items?: FneItem[];
  customTaxes?: FneTax[];
}

/** Reconnait la forme native FNE parmi les differents JSON possibles. */
export function isFneNativeExport(payload: unknown): boolean {
  const records = toRecords(payload);
  if (records.length === 0) return false;
  const first = records[0] as Record<string, unknown>;
  const hasReference = typeof first.reference === "string";
  const hasItems = Array.isArray(first.items);
  const hasTotals = "totalBeforeTaxes" in first || "totalAfterTaxes" in first;
  return hasReference && (hasItems || hasTotals);
}

export function toRecords(payload: unknown): unknown[] {
  if (Array.isArray(payload)) return payload;
  if (payload && typeof payload === "object") {
    const container = payload as Record<string, unknown>;
    for (const key of ["data", "items", "invoices", "factures", "results", "content"]) {
      const candidate = container[key];
      if (Array.isArray(candidate)) return candidate;
    }
    // Reponse de l'API de certification : { ncc, reference, token, invoice: {...} }
    if (container.invoice && typeof container.invoice === "object") return [container.invoice];
    if (typeof container.reference === "string") return [container];
  }
  return [];
}

export interface NativeParseResult {
  invoices: Invoice[];
  warnings: string[];
}

export function parseFneNative(
  payload: unknown,
  options: FneNativeOptions = DEFAULT_NATIVE_OPTIONS,
): NativeParseResult {
  const warnings: string[] = [];
  const invoices = toRecords(payload).map((record, index) =>
    buildInvoice(record as FneInvoice, index, options, warnings),
  );
  return { invoices, warnings };
}

function buildInvoice(
  source: FneInvoice,
  index: number,
  options: FneNativeOptions,
  warnings: string[],
): Invoice {
  const reference = cleanCell(source.reference);
  const kind: DocumentKind = source.subtype === "refund" ? "AVOIR" : "FACTURE";
  const signe = kind === "AVOIR" && options.avoirEnValeurAbsolue ? -1 : 1;
  const position = `Facture ${reference || `#${index + 1}`}`;

  if (!reference) {
    warnings.push(`${position} : reference FNE absente, le numero de piece sera vide.`);
  }

  const date = parseDate(source.date);
  if (!date) warnings.push(`${position} : date absente ou illisible.`);

  const items = Array.isArray(source.items) ? source.items : [];
  if (items.length === 0) {
    warnings.push(`${position} : aucun article dans l'export, une ligne de synthese sera generee.`);
  }

  const lignes = items.map((item, position2) =>
    buildLine(item, position2 + 1, signe, options, reference, warnings),
  );

  const invoice = emptyInvoice();
  invoice.numero = numeroPiece(reference, options.numeroPiece);
  invoice.numeroFne = reference;
  invoice.numeroParent = cleanCell(source.parentReference);
  invoice.codeVerification = cleanCell(source.token);
  invoice.date = date ?? "";
  invoice.kind = kind;
  invoice.devise = cleanCell(source.foreignCurrency) || "XOF";
  invoice.reference = cleanCell(source.parentReference) || reference;
  invoice.modeReglement = cleanCell(source.paymentMethod);
  invoice.template = cleanCell(source.template);
  invoice.vendeur = cleanCell(source.clientSellerName);
  invoice.pointDeVente = cleanCell(source.clientPointOfSale);
  invoice.etablissement = cleanCell(source.clientEstablishment);
  invoice.commentaire = cleanCell(source.description);
  invoice.client = {
    code: "",
    nom: cleanCell(source.clientCompanyName),
    ncc: cleanCell(source.clientNcc),
    adresse: "",
    telephone: cleanCell(source.clientPhone),
    email: cleanCell(source.clientEmail),
  };
  invoice.lignes = lignes.length > 0 ? lignes : [syntheseLine(source, signe, options)];

  const d = options.decimales;
  // Le format d'import ne porte qu'une taxe par ligne : un prelevement declare
  // en plus de la TVA (AIRSI...) n'a nulle part ou aller. Mieux vaut le dire
  // que de laisser croire que la piece est complete.
  const autresTaxes = round(signe * (source.totalCustomTaxes ?? 0), d);
  if (autresTaxes !== 0) {
    const noms = [
      ...new Set(
        [...(source.customTaxes ?? []), ...items.flatMap((item) => item.customTaxes ?? [])]
          .map((taxe) => cleanCell(taxe.shortName) || cleanCell(taxe.name))
          .filter((nom) => nom !== ""),
      ),
    ];
    warnings.push(
      `${position} : ${autresTaxes} d'autres taxes${noms.length > 0 ? ` (${noms.join(", ")})` : ""} ` +
        "ne sont pas reprises dans le fichier Sage, dont le format ne porte qu'une taxe par " +
        "ligne. A ajouter a la main sur la piece.",
    );
  }

  invoice.totaux = {
    totalHT: round(signe * (source.totalBeforeTaxes ?? 0), d),
    totalRemise: round(signe * (source.totalDiscounted ?? 0), d),
    totalTva: round(signe * (source.totalTaxes ?? source.vatAmount ?? 0), d),
    autresTaxes,
    timbre: round(signe * (source.fiscalStamp ?? 0), d),
    totalTTC: round(signe * (source.totalAfterTaxes ?? source.amount ?? 0), d),
    netAPayer: round(signe * (source.totalDue ?? source.amount ?? 0), d),
  };

  return invoice;
}

function buildLine(
  item: FneItem,
  position: number,
  signe: number,
  options: FneNativeOptions,
  reference: string,
  warnings: string[],
): InvoiceLine {
  const quantite = item.quantity ?? 0;
  // `amount` est le prix unitaire HT (annexe 1 du document DGI), pas le total de ligne.
  const prixUnitaireHT = signe * (item.amount ?? 0);
  // `discount` est interprete comme un pourcentage de remise sur l'article.
  // A CONFIRMER : aucun des exports fournis ne porte de remise non nulle.
  const remisePourcent = item.discount ?? 0;

  const tax = item.taxes?.[0];
  const codeTaxe = cleanCell(tax?.shortName) || cleanCell(tax?.name);
  const taxCode = codeTaxe ? findTaxCode(codeTaxe) : null;
  const tauxTva = tax?.amount ?? taxCode?.taux ?? 0;

  if (!tax) {
    warnings.push(`Facture ${reference}, ligne ${position} : aucune taxe declaree, taux ramene a 0 %.`);
  }
  if ((item.taxes?.length ?? 0) > 1) {
    warnings.push(
      `Facture ${reference}, ligne ${position} : ${item.taxes?.length} taxes declarees, ` +
        "seule la premiere est reprise dans Sage.",
    );
  }

  // La TVA est calculee sur le HT non arrondi : arrondir d'abord decalerait
  // le total de la ligne de quelques centimes par rapport au total FNE.
  const htBrut = quantite * prixUnitaireHT * (1 - remisePourcent / 100);
  const montantHT = round(htBrut, options.decimales);
  const montantTva = round((htBrut * tauxTva) / 100, options.decimales);

  return {
    numero: position,
    referenceArticle: cleanCell(item.reference),
    designation: cleanCell(item.description),
    quantite,
    // Le prix unitaire garde sa precision d'origine : les exports FNE portent
    // des prix a plusieurs decimales, arrondir ici decale les totaux.
    prixUnitaireHT: round(prixUnitaireHT, 6),
    remisePourcent,
    tauxTva,
    codeTaxeFne: taxCode?.code ?? codeTaxe ?? taxCodeFromRate(tauxTva),
    montantHT,
    montantTva,
    montantTTC: round(htBrut * (1 + tauxTva / 100), options.decimales),
    unite: cleanCell(item.measurementUnit),
    sourceRow: position,
  };
}

/** Ligne unique reconstituee depuis les totaux, quand l'export ne porte pas d'articles. */
function syntheseLine(source: FneInvoice, signe: number, options: FneNativeOptions): InvoiceLine {
  const totalHT = round(signe * (source.totalBeforeTaxes ?? 0), options.decimales);
  const totalTva = round(signe * (source.totalTaxes ?? source.vatAmount ?? 0), options.decimales);
  const tauxTva = totalHT !== 0 ? round((totalTva / totalHT) * 100, 2) : 0;

  return {
    numero: 1,
    referenceArticle: "",
    designation: `Facture FNE ${cleanCell(source.reference)}`,
    quantite: 1,
    prixUnitaireHT: totalHT,
    remisePourcent: 0,
    tauxTva,
    codeTaxeFne: taxCodeFromRate(tauxTva),
    montantHT: totalHT,
    montantTva: totalTva,
    montantTTC: round(totalHT + totalTva, options.decimales),
    unite: "",
    sourceRow: 1,
  };
}

/**
 * Une reference FNE est construite comme NCC + annee + numero d'ordre,
 * precedee de "A" pour un avoir : 2304903U26000000889, A2304903U2600000038.
 * Le NCC etant constant pour une entreprise, seule la partie annee + numero
 * est reprise comme numero de piece Sage (13 caracteres au maximum).
 */
export function numeroPiece(reference: string, mode: FneNativeOptions["numeroPiece"]): string {
  if (mode === "vide") return "";
  if (mode === "reference") return reference;
  return sequenceFromReference(reference);
}

export function sequenceFromReference(reference: string): string {
  const match = reference.match(/^(A?)([0-9]{7}[A-Z])(.+)$/);
  if (!match) return reference;
  return `${match[1]}${match[3]}`;
}
