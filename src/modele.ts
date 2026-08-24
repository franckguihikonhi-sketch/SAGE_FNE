/**
 * Ce qu'une facture doit porter pour devenir un document de vente Sage.
 *
 * Volontairement pauvre : tout ce que FNE certifie et que le format d'import
 * du dossier ne transporte pas - mode de paiement, vendeur, point de vente,
 * code de verification - n'a pas sa place ici. Ce qui n'est pas ecrit n'a pas
 * a etre lu.
 */

export type Nature = "FACTURE" | "AVOIR";

export interface Ligne {
  /** Reference de l'article. FNE porte deja celles du dossier Sage. */
  reference: string;
  designation: string;
  /** Unite de vente telle que la facture l'affiche (CARTON, KG, SAC...). */
  unite: string;
  quantite: number;
  /** Prix unitaire HT, a la precision de la source. */
  prixUnitaire: number;
  /** Montant HT de la ligne, tel que FNE l'a certifie. */
  montantHT: number;
  /** Code de la taxe portee par la ligne : TVA, AIRSI, ou vide si exoneree. */
  codeTaxe: string;
  /** Taux de cette taxe, en pourcentage. */
  taux: number;
}

export interface Client {
  nom: string;
  /** Numero de Compte Contribuable, absent chez les clients du secteur informel. */
  ncc: string;
  /** Compte tiers Sage, etabli par la table de correspondance. */
  compte: string;
}

export interface Facture {
  /** Reference certifiee FNE, quand la source la porte. */
  reference: string;
  /** Date du document, en ISO aaaa-mm-jj. */
  date: string;
  nature: Nature;
  client: Client;
  lignes: Ligne[];
  /** Totaux certifies, pour verifier ce qui est ecrit. */
  totalHT: number;
  totalTva: number;
  totalAutresTaxes: number;
  /** Rang de la facture dans le fichier lu, pour situer une anomalie. */
  rang: number;
}

export function totalLignes(facture: Facture): number {
  return facture.lignes.reduce((somme, ligne) => somme + ligne.montantHT, 0);
}
