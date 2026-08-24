import { Facture } from "./modele";

/**
 * Rapprochement des clients et des unites avec le dossier Sage.
 *
 * Deux nomenclatures ne se rejoignent pas toutes seules : FNE nomme le client
 * ("PROSUMA-STE IVOIRIENNE"), Sage l'attend par son compte tiers ("PROSUMA").
 * Les references d'article, elles, sont deja celles du dossier : FNE est
 * alimente depuis le meme catalogue, et il n'y a rien a traduire.
 */

export interface Correspondance {
  /** Ce que porte la facture : un NCC ou un nom de client. */
  cle: string;
  /** Ce que Sage attend. */
  valeur: string;
}

/** "5011806N;PROSUMA" ou "PROSUMA-STE IVOIRIENNE;PROSUMA", une par ligne. */
export function lireTable(texte: string): Correspondance[] {
  const entrees: Correspondance[] = [];
  for (const ligne of texte.split(/\r?\n/)) {
    if (ligne.trim() === "" || ligne.trimStart().startsWith("#")) continue;
    const [cle = "", valeur = ""] = ligne.split(/[;,\t=]/).map((cellule) => cellule.trim());
    if (cle === "" || valeur === "") continue;
    entrees.push({ cle, valeur });
  }
  return entrees;
}

/** Accents, casse et ponctuation retires : "Côte d'Ivoire" et "COTE DIVOIRE" se rejoignent. */
export function cle(valeur: string): string {
  return valeur
    .normalize("NFD")
    .replace(/[̀-ͯ]/g, "")
    .toUpperCase()
    .replace(/[^A-Z0-9]/g, "");
}

/** Unites du dossier, telles que l'exemplaire verifie les ecrit. */
export const UNITES_PAR_DEFAUT = "CARTON=CN\nCARTONS=CN\nKILOGRAM=KG\nKILOGRAMME=KG\nSAC=SAC";

export interface ClientInconnu {
  nom: string;
  ncc: string;
  factures: number;
}

export interface Rapprochement {
  factures: Facture[];
  clientsInconnus: ClientInconnu[];
  unitesInconnues: string[];
}

export function rapprocher(
  factures: Facture[],
  clients: Correspondance[],
  unites: Correspondance[],
  compteParDefaut = "",
): Rapprochement {
  const tableClients = new Map(clients.map((entree) => [cle(entree.cle), entree.valeur]));
  const tableUnites = new Map(unites.map((entree) => [cle(entree.cle), entree.valeur]));

  const inconnus = new Map<string, ClientInconnu>();
  const unitesInconnues = new Set<string>();

  const rapprochees = factures.map((facture) => {
    // Le NCC identifie mieux qu'un nom, qui change d'orthographe d'une
    // facture a l'autre ; les clients du secteur informel n'en ont pas.
    const compte =
      (facture.client.ncc ? tableClients.get(cle(facture.client.ncc)) : undefined) ??
      tableClients.get(cle(facture.client.nom)) ??
      "";

    if (compte === "") {
      const identite = cle(facture.client.ncc || facture.client.nom);
      const connu = inconnus.get(identite);
      if (connu) connu.factures += 1;
      else {
        inconnus.set(identite, { nom: facture.client.nom, ncc: facture.client.ncc, factures: 1 });
      }
    }

    return {
      ...facture,
      client: { ...facture.client, compte: compte || compteParDefaut },
      lignes: facture.lignes.map((ligne) => {
        if (ligne.unite === "") return ligne;
        const traduite = tableUnites.get(cle(ligne.unite));
        if (!traduite) unitesInconnues.add(ligne.unite);
        return { ...ligne, unite: traduite ?? ligne.unite };
      }),
    };
  });

  return {
    factures: rapprochees,
    clientsInconnus: [...inconnus.values()].sort((a, b) => b.factures - a.factures),
    unitesInconnues: [...unitesInconnues].sort(),
  };
}
