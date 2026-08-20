/**
 * Description d'un format d'import Sage 100 Gestion Commerciale
 * (Fichier > Importer > Format parametrable).
 *
 * Un profil decrit *exactement* le fichier texte attendu par le format
 * d'import (.imp) defini dans Sage. Il est volontairement 100 % declaratif
 * et serialisable en JSON : adapter le connecteur au parametrage d'un client
 * revient a editer un profil, jamais a modifier du code.
 */

export type SageSource =
  | { kind: "const"; value: string }
  | { kind: "token"; token: string }
  | { kind: "empty" };

export interface SageColumn {
  /** Libelle de la zone, tel qu'il apparait dans le format d'import Sage. */
  label: string;
  source: SageSource;
  /** Longueur imposee : troncature et remplissage (obligatoire en longueur fixe). */
  length?: number;
  align?: "left" | "right";
  pad?: string;
  /** Nombre de decimales pour les zones numeriques (surcharge le profil). */
  decimals?: number;
}

export interface SageImportProfile {
  id: string;
  label: string;
  description: string;
  /** Extension du fichier genere. */
  extension: string;
  layout: "delimited" | "fixed";
  /** Separateur de zones en mode delimite. */
  delimiter: string;
  /** Caractere d'encadrement des zones texte ("" si aucun). */
  quote: string;
  encoding: "windows-1252" | "utf-8";
  eol: "\r\n" | "\n";
  decimalSeparator: "." | ",";
  decimals: number;
  dateFormat: "DDMMYYYY" | "DD/MM/YYYY" | "YYYYMMDD" | "DDMMYY";
  /** Ligne de libelles en tete de fichier (rarement acceptee par Sage). */
  includeHeaderRow: boolean;
  /** Codes DO_Type Sage a ecrire selon la nature du document. */
  documentTypes: { facture: string; avoir: string };
  entete: SageColumn[];
  ligne: SageColumn[];
}

/**
 * Profil par defaut : documents des ventes, fichier texte tabule,
 * un enregistrement "E" par facture suivi d'un enregistrement "L" par ligne.
 *
 * A VALIDER AVEC LE PARAMETRAGE DU CLIENT. L'ordre des zones, les codes
 * DO_Type et la presence du marqueur E/L dependent du format d'import (.imp)
 * defini dans le dossier Sage. Recuperer ce .imp permet de reproduire le
 * profil a l'identique.
 */
export const SAGE100_DOCUMENTS_VENTES: SageImportProfile = {
  id: "sage100-documents-ventes",
  label: "Sage 100 GesCom - Documents des ventes (tabule)",
  description:
    "Fichier texte tabule, un enregistrement d'entete (E) par facture suivi de ses lignes (L). " +
    "Correspond au format parametrable le plus courant pour l'import des factures de vente.",
  extension: "txt",
  layout: "delimited",
  delimiter: "\t",
  quote: "",
  encoding: "windows-1252",
  eol: "\r\n",
  decimalSeparator: ".",
  decimals: 2,
  dateFormat: "DDMMYYYY",
  includeHeaderRow: false,
  // 6 = Facture, 5 = Bon d'avoir financier (nomenclature DO_Type de Sage 100).
  documentTypes: { facture: "6", avoir: "5" },
  entete: [
    { label: "Marqueur", source: { kind: "const", value: "E" } },
    { label: "Type de document", source: { kind: "token", token: "document.type" } },
    { label: "Numero de piece", source: { kind: "token", token: "document.numero" } },
    { label: "Date", source: { kind: "token", token: "document.date" } },
    { label: "Numero de compte tiers", source: { kind: "token", token: "client.code" } },
    { label: "Intitule du tiers", source: { kind: "token", token: "client.nom" } },
    { label: "Reference", source: { kind: "token", token: "document.reference" } },
    { label: "Mode de reglement", source: { kind: "token", token: "document.codeReglement" } },
    { label: "Devise", source: { kind: "token", token: "document.devise" } },
    { label: "Total HT", source: { kind: "token", token: "totaux.ht" } },
    { label: "Total TVA", source: { kind: "token", token: "totaux.tva" } },
    { label: "Total TTC", source: { kind: "token", token: "totaux.ttc" } },
    { label: "Reference FNE", source: { kind: "token", token: "document.numeroFne" } },
    { label: "Commentaire", source: { kind: "token", token: "document.commentaire" } },
  ],
  ligne: [
    { label: "Marqueur", source: { kind: "const", value: "L" } },
    { label: "Numero de piece", source: { kind: "token", token: "document.numero" } },
    { label: "Numero de ligne", source: { kind: "token", token: "ligne.numero" } },
    { label: "Reference article", source: { kind: "token", token: "ligne.reference" } },
    { label: "Designation", source: { kind: "token", token: "ligne.designation" } },
    { label: "Quantite", source: { kind: "token", token: "ligne.quantite" } },
    { label: "Prix unitaire HT", source: { kind: "token", token: "ligne.prixUnitaire" }, decimals: 4 },
    { label: "Remise (%)", source: { kind: "token", token: "ligne.remise" } },
    { label: "Taux de TVA", source: { kind: "token", token: "ligne.tauxTva" } },
    { label: "Montant HT", source: { kind: "token", token: "ligne.montantHT" } },
    { label: "Montant TVA", source: { kind: "token", token: "ligne.montantTva" } },
    { label: "Unite", source: { kind: "token", token: "ligne.unite" } },
  ],
};

/**
 * Variante a plat : une seule ligne par ligne de facture, les zones d'entete
 * etant repetees. Certains parametrages Sage preferent cette forme, plus simple
 * a decrire dans un format d'import.
 */
export const SAGE100_LIGNE_A_PLAT: SageImportProfile = {
  ...SAGE100_DOCUMENTS_VENTES,
  id: "sage100-ligne-a-plat",
  label: "Sage 100 GesCom - Une ligne par article (entete repete)",
  description:
    "Fichier texte tabule sans marqueur : chaque ligne porte les zones d'entete de la facture " +
    "suivies des zones de la ligne d'article.",
  entete: [],
  ligne: [
    { label: "Type de document", source: { kind: "token", token: "document.type" } },
    { label: "Numero de piece", source: { kind: "token", token: "document.numero" } },
    { label: "Date", source: { kind: "token", token: "document.date" } },
    { label: "Numero de compte tiers", source: { kind: "token", token: "client.code" } },
    { label: "Intitule du tiers", source: { kind: "token", token: "client.nom" } },
    { label: "Reference", source: { kind: "token", token: "document.reference" } },
    { label: "Numero de ligne", source: { kind: "token", token: "ligne.numero" } },
    { label: "Reference article", source: { kind: "token", token: "ligne.reference" } },
    { label: "Designation", source: { kind: "token", token: "ligne.designation" } },
    { label: "Quantite", source: { kind: "token", token: "ligne.quantite" } },
    { label: "Prix unitaire HT", source: { kind: "token", token: "ligne.prixUnitaire" }, decimals: 4 },
    { label: "Remise (%)", source: { kind: "token", token: "ligne.remise" } },
    { label: "Taux de TVA", source: { kind: "token", token: "ligne.tauxTva" } },
    { label: "Montant HT", source: { kind: "token", token: "ligne.montantHT" } },
    { label: "Montant TVA", source: { kind: "token", token: "ligne.montantTva" } },
    { label: "Reference FNE", source: { kind: "token", token: "document.numeroFne" } },
  ],
};

/** Profil CSV point-virgule, utile pour verifier le mappage dans Excel. */
export const SAGE100_CSV_CONTROLE: SageImportProfile = {
  ...SAGE100_LIGNE_A_PLAT,
  id: "sage100-csv-controle",
  label: "CSV de controle (point-virgule, avec entetes)",
  description:
    "Meme contenu que le profil a plat, en CSV point-virgule avec une ligne de libelles. " +
    "Sert a relire le resultat dans Excel avant l'import reel.",
  extension: "csv",
  delimiter: ";",
  decimalSeparator: ",",
  includeHeaderRow: true,
};

export const PROFILES: SageImportProfile[] = [
  SAGE100_DOCUMENTS_VENTES,
  SAGE100_LIGNE_A_PLAT,
  SAGE100_CSV_CONTROLE,
];

export function findProfile(id: string): SageImportProfile | null {
  return PROFILES.find((profile) => profile.id === id) ?? null;
}
