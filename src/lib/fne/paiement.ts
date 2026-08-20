import { normalizeKey } from "@/lib/core/text";

/**
 * Modes de paiement FNE (parametre `paymentMethod`).
 *
 * Source : "Procedure de certification des factures des entreprises par API"
 * (DGI, mai 2025), annexe 1 - lexique.
 */
export const FNE_PAYMENT_METHODS: Record<string, string> = {
  cash: "Espece",
  card: "Carte bancaire",
  check: "Cheque",
  "mobile-money": "Mobile money",
  transfer: "Virement bancaire",
  deferred: "A terme",
};

export function libellePaiement(code: string): string {
  return FNE_PAYMENT_METHODS[code.toLowerCase()] ?? code;
}

/**
 * Correspondance mode de paiement FNE -> code de reglement Sage.
 * Vide par defaut : les codes reglement sont propres a chaque dossier Sage.
 * Renseignee par l'utilisateur, elle alimente le jeton `document.codeReglement`.
 */
export type PaymentMapping = Record<string, string>;

export function codeReglementSage(code: string, mapping: PaymentMapping): string {
  return mapping[code.toLowerCase()] ?? mapping[normalizeKey(code)] ?? "";
}

/**
 * Lit une correspondance saisie ligne a ligne : `deferred=CRED`, `cash;ESP`,
 * `mobile-money,MOMO`. Les codes FNE inconnus sont conserves tels quels.
 */
export function parsePaymentMappingText(text: string): PaymentMapping {
  const mapping: PaymentMapping = {};
  for (const line of text.split(/\r?\n/)) {
    const cells = line.split(/[=;,\t]/).map((cell) => cell.trim());
    const [code, valeur] = cells;
    if (!code || !valeur) continue;
    mapping[code.toLowerCase()] = valeur;
  }
  return mapping;
}

/** Types de facturation FNE (parametre `template`). */
export const FNE_TEMPLATES: Record<string, string> = {
  B2B: "Client professionnel possedant un NCC",
  B2C: "Client particulier",
  B2G: "Institution gouvernementale",
  B2F: "Client a l'etranger",
};

/** Types de facture FNE (parametre `invoiceType`). */
export const FNE_INVOICE_TYPES: Record<string, string> = {
  sale: "Vente",
  purchase: "Achat",
};
