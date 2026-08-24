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
  /** Valeur numerique fixe, mise en forme selon les decimales et le separateur du profil. */
  | { kind: "nombre"; value: number }
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
  /**
   * Enregistrement ecrit une fois apres les lignes de chaque document.
   *
   * Les deux fichiers de reference du dossier client se terminent par une telle
   * ligne : elle reprend le type, la souche, la date de livraison et le compte
   * tiers du document, sans article ni depot. Elle clot le document.
   */
  pied?: SageColumn[];
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


/**
 * Profil calque sur l'exemplaire que le dossier client importe sans difficulte
 * ("EXPORT_SAGE_VERIFIE.txt", 60 enregistrements).
 *
 * Quatorze zones tabulees, fins de ligne CRLF, Windows-1252, dates jjmmaa,
 * separateur decimal virgule. Les zones, relevees une a une sur l'exemplaire :
 *
 *  1  vide sur les soixante lignes          8  reference article
 *  2  date du document                      9  designation
 *  3  depot                                10  prix unitaire (6 decimales)
 *  4  type de document (6, 8...)           11  quantite (4 decimales)
 *  5  numero de piece (443, FR00035...)    12  unite (CN, KG, SAC)
 *  6  date de livraison                    13  code taxe (TVA, AIRSI, vide)
 *  7  compte tiers                         14  taux de TVA (4 decimales)
 *
 * Deux ecarts avec le fichier d'export de reference precedent expliquent le
 * refus de Sage a l'import :
 *
 * - ce dernier porte une zone de plus en tete, une constante "0" ; tout est
 *   alors decale d'un cran et Sage lit le depot la ou il attend le type de
 *   document, d'ou "Le champ Type document est incorrect a la ligne 1" ;
 * - sa derniere zone n'est pas une remise mais le taux de TVA, et
 *   l'avant-derniere le code taxe. **Ce format transporte donc la taxe** :
 *   le taux de chaque ligne est ecrit, et non deduit de la fiche article.
 */
export const SAGE100_EXPORT_VERIFIE: SageImportProfile = {
  id: "sage100-export-verifie",
  label: "Sage 100 GesCom - Export verifie du dossier (14 zones)",
  description:
    "Reproduction de l'exemplaire que le dossier importe sans difficulte : 14 zones tabulees, " +
    "format a plat, dates jjmmaa, virgule decimale, Windows-1252, taxe portee par la ligne.",
  extension: "txt",
  layout: "delimited",
  delimiter: "\t",
  quote: "",
  encoding: "windows-1252",
  eol: "\r\n",
  decimalSeparator: ",",
  decimals: 2,
  dateFormat: "DDMMYY",
  includeHeaderRow: false,
  documentTypes: { facture: "6", avoir: "5" },
  entete: [],
  ligne: [
    // 1 - Vide sur les soixante lignes de l'exemplaire.
    { label: "Zone 1", source: { kind: "empty" } },
    { label: "Date du document", source: { kind: "token", token: "document.date" } },
    { label: "Depot", source: { kind: "token", token: "parametre.depot" } },
    { label: "Type de document", source: { kind: "token", token: "document.type" } },
    { label: "Numero de piece", source: { kind: "token", token: "document.numero" } },
    { label: "Date de livraison", source: { kind: "token", token: "document.dateLivraison" } },
    { label: "Compte tiers", source: { kind: "token", token: "client.code" } },
    { label: "Reference article", source: { kind: "token", token: "ligne.reference" } },
    { label: "Designation", source: { kind: "token", token: "ligne.designation" } },
    { label: "Prix unitaire HT", source: { kind: "token", token: "ligne.prixUnitaire" }, decimals: 6 },
    { label: "Quantite", source: { kind: "token", token: "ligne.quantite" }, decimals: 4 },
    { label: "Unite", source: { kind: "token", token: "ligne.unite" } },
    { label: "Code taxe", source: { kind: "token", token: "ligne.codeTaxeSage" } },
    { label: "Taux de TVA", source: { kind: "token", token: "ligne.tauxTva" }, decimals: 4 },
  ],
  // Ligne de cloture, relevee telle quelle sur l'exemplaire : ni date de
  // document, ni depot, ni article, mais le type, le numero, la date de
  // livraison et le compte tiers du document.
  pied: [
    { label: "Zone 1", source: { kind: "empty" } },
    { label: "Date du document", source: { kind: "empty" } },
    { label: "Depot", source: { kind: "empty" } },
    { label: "Type de document", source: { kind: "token", token: "document.type" } },
    { label: "Numero de piece", source: { kind: "token", token: "document.numero" } },
    { label: "Date de livraison", source: { kind: "token", token: "document.dateLivraison" } },
    { label: "Compte tiers", source: { kind: "token", token: "client.code" } },
    { label: "Reference article", source: { kind: "empty" } },
    { label: "Designation", source: { kind: "empty" } },
    { label: "Prix unitaire HT", source: { kind: "nombre", value: 0 }, decimals: 6 },
    { label: "Quantite", source: { kind: "nombre", value: 0 }, decimals: 4 },
    { label: "Unite", source: { kind: "empty" } },
    { label: "Code taxe", source: { kind: "empty" } },
    { label: "Taux de TVA", source: { kind: "nombre", value: 0 }, decimals: 4 },
  ],
};

/**
 * Profil calque sur le format d'import/export du dossier client
 * ("FORMAT IMPORT_EXPORT", fichier .egc) et sur le fichier d'exemple fourni.
 *
 * Caracteristiques relevees sur le fichier reel :
 * - texte tabule, fins de ligne CRLF, encodage Windows-1252 ;
 * - 15 zones, format *a plat* : les zones d'entete sont repetees sur chaque
 *   ligne d'article, Sage regroupant les lignes en documents ;
 * - dates au format jjmmaa (200826 = 20/08/2026), conformement au format ;
 * - separateur decimal virgule, prix unitaire a 6 decimales, quantite et
 *   remise a 4 decimales.
 *
 * Le format .egc declare 19 zones dont 15 retenues, dans l'ordre
 * 0-6, 11-16, 20, 21 : c'est exactement le nombre de colonnes du fichier
 * d'exemple. Les zones 7, 8, 17 et 18 ne sont pas reprises.
 *
 * ATTENTION : ce format ne comporte aucune zone de taxe. Sage appliquera le
 * regime de TVA parametre sur chaque article, et non le code taxe FNE porte
 * par la facture. Voir docs/format-import-sage.md.
 */
export const SAGE100_IMPORT_EXPORT: SageImportProfile = {
  id: "sage100-import-export",
  label: "Sage 100 GesCom - FORMAT IMPORT_EXPORT (15 zones, export seul)",
  description:
    "Reproduction du fichier d'export du dossier : 15 zones tabulees, une constante 0 en tete. " +
    "Sage refuse ce fichier a l'import, y compris quand c'est son propre export - la zone de tete " +
    "decale tout d'un cran. Conserve pour comparaison.",
  extension: "txt",
  layout: "delimited",
  delimiter: "\t",
  quote: "",
  encoding: "windows-1252",
  eol: "\r\n",
  decimalSeparator: ",",
  decimals: 2,
  dateFormat: "DDMMYY",
  includeHeaderRow: false,
  documentTypes: { facture: "6", avoir: "5" },
  // Format a plat : aucune ligne d'entete distincte.
  entete: [],
  ligne: [
    // 1 - Constante 0 dans le fichier d'exemple : domaine Vente.
    { label: "Domaine", source: { kind: "const", value: "0" } },
    // 2 - Vide dans l'exemple (numerotation automatique par Sage). Le numero
    //     FNE est ecrit ici pour garder le lien avec la facture certifiee.
    { label: "Numero de piece", source: { kind: "token", token: "document.numero" } },
    { label: "Date du document", source: { kind: "token", token: "document.date" } },
    { label: "Depot", source: { kind: "token", token: "parametre.depot" } },
    { label: "Type de document", source: { kind: "token", token: "document.type" } },
    // 6 - Constante 1 dans l'exemple : souche du document.
    { label: "Souche", source: { kind: "token", token: "parametre.souche" } },
    { label: "Date de livraison", source: { kind: "token", token: "document.dateLivraison" } },
    { label: "Compte tiers", source: { kind: "token", token: "client.code" } },
    { label: "Reference article", source: { kind: "token", token: "ligne.reference" } },
    { label: "Designation", source: { kind: "token", token: "ligne.designation" } },
    { label: "Prix unitaire HT", source: { kind: "token", token: "ligne.prixUnitaire" }, decimals: 6 },
    { label: "Quantite", source: { kind: "token", token: "ligne.quantite" }, decimals: 4 },
    { label: "Unite", source: { kind: "token", token: "ligne.unite" } },
    // 14 - Zone vide dans le fichier d'exemple, non identifiee.
    { label: "Zone 14", source: { kind: "empty" } },
    { label: "Remise", source: { kind: "token", token: "ligne.remise" }, decimals: 4 },
  ],
  // Ligne de cloture, calquee sur les fichiers de reference : seules les zones
  // qui identifient le document sont reprises, sans date de document, sans
  // depot et sans article.
  pied: [
    { label: "Domaine", source: { kind: "const", value: "0" } },
    { label: "Numero de piece", source: { kind: "token", token: "document.numero" } },
    { label: "Date du document", source: { kind: "empty" } },
    { label: "Depot", source: { kind: "empty" } },
    { label: "Type de document", source: { kind: "token", token: "document.type" } },
    { label: "Souche", source: { kind: "token", token: "parametre.souche" } },
    { label: "Date de livraison", source: { kind: "token", token: "document.dateLivraison" } },
    { label: "Compte tiers", source: { kind: "token", token: "client.code" } },
    { label: "Reference article", source: { kind: "empty" } },
    { label: "Designation", source: { kind: "empty" } },
    { label: "Prix unitaire HT", source: { kind: "nombre", value: 0 }, decimals: 6 },
    { label: "Quantite", source: { kind: "nombre", value: 0 }, decimals: 4 },
    { label: "Unite", source: { kind: "empty" } },
    { label: "Zone 14", source: { kind: "empty" } },
    { label: "Remise", source: { kind: "nombre", value: 0 }, decimals: 4 },
  ],
};

/**
 * Meme parametrage, mais en deux enregistrements par document.
 *
 * La documentation Sage decrit le fichier d'import des documents des ventes
 * ainsi : "chaque document est compose de deux parties : une ligne avec les
 * informations de l'entete et pied de la piece, et une ou plusieurs lignes
 * avec les informations du detail". Le fichier d'export du dossier, lui, est
 * a plat : les quinze zones sont repetees sur chaque ligne.
 *
 * Les quinze zones retenues par le format du dossier se partagent exactement
 * entre les deux enregistrements : huit d'entete (domaine, numero, date,
 * depot, type, souche, date de livraison, compte tiers) et sept de detail
 * (article, designation, prix, quantite, unite, zone 20, remise). C'est ce
 * decoupage que reproduit ce profil, a essayer quand Sage refuse le fichier
 * a plat des sa premiere ligne.
 */
export const SAGE100_ENTETE_DETAIL: SageImportProfile = {
  ...SAGE100_IMPORT_EXPORT,
  id: "sage100-entete-detail",
  label: "Sage 100 GesCom - Entete + lignes (8 zones puis 7)",
  description:
    "Meme parametrage que FORMAT IMPORT_EXPORT, mais un enregistrement d'entete de 8 zones par " +
    "document suivi d'un enregistrement de 7 zones par ligne, comme decrit par la documentation Sage.",
  entete: [
    { label: "Domaine", source: { kind: "const", value: "0" } },
    { label: "Numero de piece", source: { kind: "token", token: "document.numero" } },
    { label: "Date du document", source: { kind: "token", token: "document.date" } },
    { label: "Depot", source: { kind: "token", token: "parametre.depot" } },
    { label: "Type de document", source: { kind: "token", token: "document.type" } },
    { label: "Souche", source: { kind: "token", token: "parametre.souche" } },
    { label: "Date de livraison", source: { kind: "token", token: "document.dateLivraison" } },
    { label: "Compte tiers", source: { kind: "token", token: "client.code" } },
  ],
  ligne: [
    { label: "Reference article", source: { kind: "token", token: "ligne.reference" } },
    { label: "Designation", source: { kind: "token", token: "ligne.designation" } },
    { label: "Prix unitaire HT", source: { kind: "token", token: "ligne.prixUnitaire" }, decimals: 6 },
    { label: "Quantite", source: { kind: "token", token: "ligne.quantite" }, decimals: 4 },
    { label: "Unite", source: { kind: "token", token: "ligne.unite" } },
    { label: "Zone 20", source: { kind: "empty" } },
    { label: "Remise", source: { kind: "token", token: "ligne.remise" }, decimals: 4 },
  ],
  // Le pied voyage sur la ligne d'entete : aucune ligne de cloture.
  pied: undefined,
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
  SAGE100_EXPORT_VERIFIE,
  SAGE100_IMPORT_EXPORT,
  SAGE100_ENTETE_DETAIL,
  SAGE100_DOCUMENTS_VENTES,
  SAGE100_LIGNE_A_PLAT,
  SAGE100_CSV_CONTROLE,
];

/** Jetons de taxe : un profil qui n'en porte aucun laisse Sage appliquer le regime de l'article. */
const TAX_TOKENS = [
  "ligne.tauxTva",
  "ligne.codeTaxe",
  "ligne.codeTaxeSage",
  "ligne.montantTva",
  "totaux.tva",
];

export function porteLaTaxe(profile: SageImportProfile): boolean {
  return [...profile.entete, ...profile.ligne].some(
    (column) => column.source.kind === "token" && TAX_TOKENS.includes(column.source.token),
  );
}

export function findProfile(id: string): SageImportProfile | null {
  return PROFILES.find((profile) => profile.id === id) ?? null;
}
