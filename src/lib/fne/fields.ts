import { normalizeKey } from "@/lib/core/text";

/**
 * Champs reconnus dans un export FNE.
 *
 * ATTENTION : les libelles de colonnes ci-dessous sont des alias *candidats*.
 * Ils couvrent les variantes rencontrees (FR/EN, avec ou sans accents) mais
 * doivent etre confrontes a un export reel : toute colonne non reconnue est
 * remontee a l'utilisateur qui la mappe manuellement dans l'interface.
 */
export type FneField =
  | "numeroFacture"
  | "numeroFne"
  | "codeVerification"
  | "dateFacture"
  | "typeDocument"
  | "sousTypeDocument"
  | "referenceParent"
  | "template"
  | "vendeur"
  | "pointDeVente"
  | "etablissement"
  | "clientCode"
  | "clientNom"
  | "clientNcc"
  | "clientAdresse"
  | "clientTelephone"
  | "clientEmail"
  | "devise"
  | "reference"
  | "modeReglement"
  | "ligneNumero"
  | "articleReference"
  | "articleDesignation"
  | "quantite"
  | "unite"
  | "prixUnitaireHT"
  | "remisePourcent"
  | "remiseMontant"
  | "codeTaxe"
  | "tauxTva"
  | "montantHT"
  | "montantTva"
  | "montantTTC"
  | "totalHT"
  | "totalRemise"
  | "totalTva"
  | "autresTaxes"
  | "timbre"
  | "totalTTC"
  | "netAPayer";

/**
 * Colonnes presentes dans l'export tableur FNE mais sans usage pour un import
 * Sage : elles sont signalees comme ignorees plutot que comme non reconnues.
 */
export const IGNORED_COLUMNS: string[] = [
  "terminal", "rccm", "regime d imposition", "autres mentions", "pied de page",
  "est rne", "rne", "cree a", "mise a jour a", "taux de change", "createdat", "updatedat",
  "id", "parentid", "statut", "status", "source", "nom du commercant", "clientmerchantname",
];

/** Un champ est soit au niveau facture (entete), soit au niveau ligne. */
export const HEADER_FIELDS: ReadonlySet<FneField> = new Set<FneField>([
  "numeroFacture", "numeroFne", "codeVerification", "dateFacture", "typeDocument",
  "sousTypeDocument", "referenceParent", "template", "vendeur", "pointDeVente",
  "etablissement", "clientCode", "clientNom", "clientNcc", "clientAdresse",
  "clientTelephone", "clientEmail", "devise", "reference", "modeReglement",
  "totalHT", "totalRemise", "totalTva", "autresTaxes", "timbre", "totalTTC", "netAPayer",
]);

/** Champs porteurs du detail des articles : leur absence declenche le mode synthese. */
export const LINE_AMOUNT_FIELDS: FneField[] = [
  "articleDesignation", "articleReference", "quantite", "prixUnitaireHT", "montantHT",
];

export const FIELD_LABELS: Record<FneField, string> = {
  numeroFacture: "Numero de facture",
  numeroFne: "Numero FNE / identifiant certifie",
  codeVerification: "Code de verification (QR)",
  dateFacture: "Date de facture",
  typeDocument: "Type de facture (vente / achat)",
  sousTypeDocument: "Sous-type de facture (normal / avoir)",
  referenceParent: "Reference de la facture d'origine",
  template: "Type de facturation (B2B, B2C, B2G, B2F)",
  vendeur: "Nom du vendeur",
  pointDeVente: "Point de vente",
  etablissement: "Etablissement",
  clientCode: "Code client",
  clientNom: "Nom / raison sociale du client",
  clientNcc: "NCC du client",
  clientAdresse: "Adresse du client",
  clientTelephone: "Telephone du client",
  clientEmail: "Email du client",
  devise: "Devise",
  reference: "Reference du document",
  modeReglement: "Mode de reglement",
  ligneNumero: "Numero de ligne",
  articleReference: "Reference article",
  articleDesignation: "Designation article",
  quantite: "Quantite",
  unite: "Unite",
  prixUnitaireHT: "Prix unitaire HT",
  remisePourcent: "Remise (%)",
  remiseMontant: "Remise (montant)",
  codeTaxe: "Code taxe FNE",
  tauxTva: "Taux de TVA",
  montantHT: "Montant HT de la ligne",
  montantTva: "Montant TVA de la ligne",
  montantTTC: "Montant TTC de la ligne",
  totalHT: "Total HT de la facture",
  totalRemise: "Total remise de la facture",
  totalTva: "Total TVA de la facture",
  autresTaxes: "Total autres taxes",
  timbre: "Timbre de quittance",
  totalTTC: "Total TTC de la facture",
  netAPayer: "Net a payer",
};

/**
 * Alias de colonnes, du plus specifique au plus generique.
 * L'ordre compte : le premier alias qui correspond gagne.
 */
export const FIELD_ALIASES: Record<FneField, string[]> = {
  numeroFacture: [
    // Libelle de l'export tableur FNE
    "reference",
    "numero facture", "numero de facture", "n facture", "no facture", "num facture",
    "numero piece", "numero du document", "invoice number", "reference facture", "facture",
  ],
  numeroFne: [
    "numero fne", "n fne", "identifiant fne", "reference fne", "numero certification",
    "numero certifie", "fne number", "invoice id", "id fne", "numero normalise",
  ],
  codeVerification: [
    "token",
    "code verification", "cle de verification", "code de verification", "qr code",
    "qr", "verification code", "sticker", "code securite",
  ],
  dateFacture: [
    "date facture", "date de facture", "date du document", "date emission",
    "date d emission", "invoice date", "date",
  ],
  typeDocument: [
    "type de facture", "type document", "type de document", "type de piece",
    "nature du document", "type facture", "invoice type", "type",
  ],
  sousTypeDocument: [
    "sous type de facture", "sous type facture", "sous type", "subtype", "soustype",
  ],
  referenceParent: [
    "reference initial", "reference initiale", "reference d origine", "parent reference",
    "facture d origine",
  ],
  template: ["type de facturation", "template", "type facturation"],
  vendeur: ["nom du vendeur", "vendeur", "seller name", "commercial"],
  pointDeVente: ["point de vente", "pointofsale", "point vente"],
  etablissement: ["etablissement", "establishment"],
  clientCode: [
    "code client", "compte client", "numero client", "customer code", "client code",
    "code tiers", "compte tiers",
  ],
  clientNom: [
    "nom de la societe du client", "nom de la societe client", "nom du client",
    "nom client", "raison sociale", "client nom", "nom du client", "denomination",
    "customer name", "client name", "designation client", "client",
  ],
  clientNcc: [
    "ncc du client", "ncc client", "numero compte contribuable", "compte contribuable",
    "ncc", "nif", "identifiant fiscal", "taxpayer number",
  ],
  clientAdresse: ["adresse client", "adresse du client", "adresse", "customer address"],
  clientTelephone: ["telephone client", "telephone", "tel", "contact", "phone"],
  clientEmail: ["email client", "email", "mail", "courriel"],
  devise: ["devises etrangeres", "devise etrangere", "devise", "currency", "monnaie"],
  reference: ["reference document", "reference", "votre reference", "ref"],
  modeReglement: [
    "mode de paiement",
    "mode reglement", "mode de reglement", "mode de paiement", "moyen de paiement",
    "payment method", "reglement",
  ],
  ligneNumero: ["numero ligne", "n ligne", "no ligne", "rang", "line number", "ligne"],
  articleReference: [
    "reference article", "code article", "ref article", "item code", "product code",
    "sku", "code produit",
  ],
  articleDesignation: [
    "designation article", "libelle article", "designation", "libelle", "description",
    "item name", "product name", "nom article", "article",
  ],
  quantite: ["quantite", "qte", "quantity", "qty", "nombre"],
  unite: ["unite", "unite de mesure", "unit", "uom"],
  prixUnitaireHT: [
    "prix unitaire ht", "pu ht", "prix unitaire hors taxe", "prix unitaire",
    "unit price", "pu", "prix",
  ],
  remisePourcent: ["remise pourcentage", "taux remise", "remise en pourcentage", "discount rate", "remise pct"],
  remiseMontant: ["montant remise", "remise montant", "remise", "discount", "rabais"],
  codeTaxe: ["code taxe", "code tva", "type taxe", "tax code", "categorie taxe", "taxe"],
  tauxTva: ["taux tva", "taux de tva", "tva pourcentage", "vat rate", "taux"],
  montantHT: [
    "montant ht ligne", "total ht ligne", "montant hors taxe", "montant ht",
    "base ht", "amount excl tax", "ht",
  ],
  montantTva: ["montant tva ligne", "montant tva", "tva ligne", "vat amount", "tva"],
  montantTTC: [
    "montant ttc ligne", "total ttc ligne", "montant ttc", "amount incl tax", "ttc",
  ],
  totalHT: ["total ht facture", "total ht", "total hors taxe", "montant total ht", "sous total"],
  totalRemise: ["total remise", "remise globale", "remise totale"],
  totalTva: ["total tva facture", "total tva", "montant total tva", "total taxes"],
  autresTaxes: ["total autres taxes", "autres taxes", "aib", "acompte impot", "taxe specifique"],
  timbre: ["timbre de quittance", "timbre", "droit de timbre", "fiscal stamp"],
  netAPayer: ["net a payer", "total du", "montant du", "total due"],
  totalTTC: [
    "total ttc facture", "total ttc", "montant total ttc", "total a payer",
    "montant total", "total",
  ],
};

const ALIAS_INDEX: Array<{ alias: string; field: FneField }> = Object.entries(FIELD_ALIASES)
  .flatMap(([field, aliases]) => aliases.map((alias) => ({ alias: normalizeKey(alias), field: field as FneField })))
  // Les alias les plus longs sont testes en premier pour eviter que "tva"
  // ne capture une colonne "montant tva ligne".
  .sort((a, b) => b.alias.length - a.alias.length);

/**
 * Devine le champ correspondant a un libelle de colonne.
 * Retourne `null` si aucun alias ne correspond : la colonne sera proposee
 * a l'utilisateur pour un mappage manuel.
 */
export function guessField(columnLabel: string): FneField | null {
  const key = normalizeKey(columnLabel);
  if (!key) return null;
  for (const entry of ALIAS_INDEX) {
    if (key === entry.alias) return entry.field;
  }
  for (const entry of ALIAS_INDEX) {
    if (key.includes(entry.alias)) return entry.field;
  }
  return null;
}
