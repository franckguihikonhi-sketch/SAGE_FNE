import { encodeCp1252 } from "./cp1252";
import { Facture } from "./modele";

/**
 * Ecriture du fichier d'import du dossier : quatorze zones tabulees.
 *
 * Le format n'est pas devine, il est releve sur l'exemplaire que le dossier
 * importe sans difficulte. Les tests le rejouent : l'exemplaire relu puis
 * reecrit par ces lignes doit ressortir octet pour octet.
 *
 *  1  vide                          8  reference article
 *  2  date du document (jjmmaa)     9  designation
 *  3  depot                        10  prix unitaire (6 decimales)
 *  4  type de document             11  quantite (4 decimales)
 *  5  numero de piece              12  unite
 *  6  date de livraison            13  code taxe
 *  7  compte tiers                 14  taux de la taxe (4 decimales)
 */

export interface Reglages {
  /** Depot, zone 3. Repris tel quel sur chaque ligne. */
  depot: string;
  /** Code Sage du type de document. */
  typeFacture: string;
  typeAvoir: string;
  /** Ecrire la reference FNE en zone 5, ou laisser Sage numeroter. */
  numeroPiece: "vide" | "reference";
}

export const REGLAGES_PAR_DEFAUT: Reglages = {
  depot: "",
  typeFacture: "6",
  typeAvoir: "5",
  numeroPiece: "vide",
};

export interface FichierSage {
  nom: string;
  /** Contenu lisible, pour l'affichage. */
  texte: string;
  octets: Uint8Array;
  enregistrements: number;
}

export function ecrireSage(
  factures: Facture[],
  reglages: Reglages,
  nomDeBase = "import-sage",
): FichierSage {
  const enregistrements: string[] = [];

  for (const facture of factures) {
    const jour = jjmmaa(facture.date);
    const type = facture.nature === "AVOIR" ? reglages.typeAvoir : reglages.typeFacture;
    const piece = reglages.numeroPiece === "reference" ? facture.reference : "";

    for (const ligne of facture.lignes) {
      enregistrements.push(
        [
          "",
          jour,
          reglages.depot,
          type,
          piece,
          jour,
          facture.client.compte,
          ligne.reference,
          ligne.designation,
          decimal(ligne.prixUnitaire, 6),
          decimal(ligne.quantite, 4),
          ligne.unite,
          ligne.codeTaxe,
          decimal(ligne.taux, 4),
        ].join("\t"),
      );
    }
  }

  const texte = enregistrements.map((ligne) => `${ligne}\r\n`).join("");
  return {
    nom: `${nomDeBase}.txt`,
    texte,
    octets: encodeCp1252(texte),
    enregistrements: enregistrements.length,
  };
}

/** 2026-08-20 devient 200826, la forme que le format attend. */
export function jjmmaa(iso: string): string {
  const trouve = iso.match(/^(\d{4})-(\d{2})-(\d{2})$/);
  if (!trouve) return "";
  return `${trouve[3]}${trouve[2]}${trouve[1]!.slice(2)}`;
}

/** Virgule decimale et nombre de decimales fixe, comme dans l'exemplaire. */
export function decimal(valeur: number, decimales: number): string {
  return valeur.toFixed(decimales).replace(".", ",");
}
