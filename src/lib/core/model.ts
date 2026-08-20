/**
 * Modele pivot : represente une facture independamment du format d'export FNE
 * en entree et du format d'import Sage en sortie.
 *
 * Tout le pipeline est : export FNE -> modele pivot -> fichier d'import Sage.
 * Ajouter un nouveau format d'export (ou une nouvelle version de FNE) ne doit
 * toucher que la couche `lib/fne`, jamais `lib/sage`.
 */

export type DocumentKind = "FACTURE" | "AVOIR";

export interface InvoiceLine {
  /** Rang de la ligne dans la facture (1..n). */
  numero: number;
  referenceArticle: string;
  designation: string;
  quantite: number;
  prixUnitaireHT: number;
  /** Remise en pourcentage appliquee a la ligne (0 si aucune). */
  remisePourcent: number;
  /** Taux de TVA en pourcentage : 18, 9, 0... */
  tauxTva: number;
  /** Code taxe tel qu'il figure dans l'export FNE (TVA, TVAB, TVAC, TVAD, EXO...). */
  codeTaxeFne: string;
  montantHT: number;
  montantTva: number;
  montantTTC: number;
  /** Unite de vente si l'export la fournit. */
  unite: string;
  /** Numero de ligne du fichier source, pour tracer les anomalies. */
  sourceRow: number;
}

export interface Customer {
  /** Code client dans Sage. Vide tant qu'aucune correspondance n'a ete etablie. */
  code: string;
  nom: string;
  /** Numero de Compte Contribuable (identifiant fiscal ivoirien). */
  ncc: string;
  adresse: string;
  telephone: string;
  email: string;
}

export interface InvoiceTotals {
  totalHT: number;
  totalRemise: number;
  totalTva: number;
  /** Autres taxes declarees a la ligne `customTaxes` de FNE. */
  autresTaxes: number;
  /** Timbre de quittance (fiscalStamp). */
  timbre: number;
  totalTTC: number;
  /** Net a payer (totalDue) : TTC + timbre. */
  netAPayer: number;
}

export interface Invoice {
  /** Numero de piece repris dans Sage (reference certifiee FNE). */
  numero: string;
  /** Reference certifiee FNE (identique a `numero` sauf renumerotation). */
  numeroFne: string;
  /** Reference de la facture d'origine, pour un avoir (parentReference). */
  numeroParent: string;
  /** URL / jeton de verification FNE (QR code). */
  codeVerification: string;
  date: string; // ISO yyyy-mm-dd
  kind: DocumentKind;
  client: Customer;
  devise: string;
  reference: string;
  /** Mode de reglement, code FNE (cash, card, check, mobile-money, transfer, deferred). */
  modeReglement: string;
  /** Type de facturation FNE : B2B, B2C, B2G, B2F. */
  template: string;
  /** Nom du vendeur, du point de vente et de l'etablissement (tracabilite FNE). */
  vendeur: string;
  pointDeVente: string;
  etablissement: string;
  commentaire: string;
  lignes: InvoiceLine[];
  totaux: InvoiceTotals;
}

export function emptyCustomer(): Customer {
  return { code: "", nom: "", ncc: "", adresse: "", telephone: "", email: "" };
}

export function emptyTotals(): InvoiceTotals {
  return {
    totalHT: 0,
    totalRemise: 0,
    totalTva: 0,
    autresTaxes: 0,
    timbre: 0,
    totalTTC: 0,
    netAPayer: 0,
  };
}

export function emptyInvoice(): Invoice {
  return {
    numero: "",
    numeroFne: "",
    numeroParent: "",
    codeVerification: "",
    date: "",
    kind: "FACTURE",
    client: emptyCustomer(),
    devise: "XOF",
    reference: "",
    modeReglement: "",
    template: "",
    vendeur: "",
    pointDeVente: "",
    etablissement: "",
    commentaire: "",
    lignes: [],
    totaux: emptyTotals(),
  };
}
