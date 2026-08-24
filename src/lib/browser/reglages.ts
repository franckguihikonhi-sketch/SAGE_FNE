/**
 * Reglages du poste, conserves d'une session a l'autre.
 *
 * Un comptable convertit ses factures chaque semaine avec le meme depot, la
 * meme souche et la meme table de correspondance clients : les ressaisir a
 * chaque fois est la principale friction de l'outil. Tout reste local au
 * navigateur, aucune donnee ne part sur un serveur.
 */

import { COLONNES_RETENUES_DEFAUT } from "@/lib/fne/colonnes";

const CLE = "passerelle-fne-sage.reglages.v1";

export interface Reglages {
  profil: string;
  depot: string;
  souche: string;
  numeroPiece: string;
  /** Code Sage du type de document, vide pour garder celui du profil. */
  typeFacture: string;
  typeAvoir: string;
  compteDefaut: string;
  /** "document", "vide", ou une date jj/mm/aaaa imposee a tout le fichier. */
  dateLivraison: string;
  /** Date imposee quand `dateLivraison` vaut "fixe". */
  dateLivraisonFixe: string;
  /** Format des dates : vide pour celui du profil. */
  formatDate: string;
  /**
   * Article de synthese par taux de TVA, une ligne par taux : `18=DIVERS18`.
   * Les taux listes sont ceux que l'entreprise pratique.
   */
  articlesTaux: string;
  /** Lettres des colonnes retenues dans l'export tableur FNE. Vide : toutes. */
  colonnes: string;
  /** "oui" pour ajouter les colonnes A, C, E et G aux colonnes retenues. */
  colonnesComplement: string;
  /** Table de correspondance clients, au format CSV `ncc;nom;compte`. */
  clients: string;
  /** Correspondance des articles, au format CSV `referenceFne;referenceSage`. */
  articles: string;
  /** Correspondance des modes de reglement, une ligne par code FNE. */
  reglements: string;
}

export const REGLAGES_PAR_DEFAUT: Reglages = {
  profil: "sage100-export-verifie",
  depot: "",
  souche: "1",
  numeroPiece: "vide",
  typeFacture: "",
  typeAvoir: "",
  compteDefaut: "",
  dateLivraison: "document",
  dateLivraisonFixe: "",
  formatDate: "",
  articlesTaux: "",
  colonnes: COLONNES_RETENUES_DEFAUT,
  colonnesComplement: "oui",
  clients: "",
  articles: "",
  reglements: "",
};

export function lireReglages(): Reglages {
  try {
    const brut = localStorage.getItem(CLE);
    if (!brut) return { ...REGLAGES_PAR_DEFAUT };
    const stocke = JSON.parse(brut) as Partial<Reglages> & Record<string, unknown>;
    // Fusion avec les valeurs par defaut : une version anterieure peut ne pas
    // porter tous les champs.
    return { ...REGLAGES_PAR_DEFAUT, ...stocke, articlesTaux: articlesTaux(stocke) };
  } catch {
    return { ...REGLAGES_PAR_DEFAUT };
  }
}

/**
 * Les deux champs d'article de synthese ont laisse place a une table
 * `taux=article`, l'entreprise pouvant pratiquer plus de deux taux. Un poste
 * qui a deja des reglages ne doit pas les perdre a la mise a jour.
 */
function articlesTaux(stocke: Record<string, unknown>): string {
  const existant = stocke.articlesTaux;
  if (typeof existant === "string" && existant.trim() !== "") return existant;

  const normal = typeof stocke.articleSynthese === "string" ? stocke.articleSynthese.trim() : "";
  const exonere =
    typeof stocke.articleSyntheseExonere === "string" ? stocke.articleSyntheseExonere.trim() : "";
  const lignes = [normal ? `18=${normal}` : "", exonere ? `0=${exonere}` : ""].filter(Boolean);
  return lignes.join("\n");
}

export function ecrireReglages(reglages: Reglages): boolean {
  try {
    localStorage.setItem(CLE, JSON.stringify(reglages));
    return true;
  } catch {
    // Navigation privee ou stockage plein : l'outil reste utilisable sans memoire.
    return false;
  }
}

export function oublierReglages(): void {
  try {
    localStorage.removeItem(CLE);
  } catch {
    // Rien a faire : il n'y avait rien a oublier.
  }
}

/**
 * Ajoute des correspondances article a la table existante, en remplacant celles
 * qui portent deja la meme reference FNE.
 */
export function fusionnerArticles(
  csv: string,
  ajouts: Array<{ referenceFne: string; referenceSage: string }>,
): string {
  const lignes = csv.split(/\r?\n/).filter((ligne) => ligne.trim() !== "");
  const cle = (ligne: string) => (ligne.split(/[;,\t]/)[0] ?? "").trim().toLowerCase();

  const conserves = lignes.filter(
    (ligne) => !ajouts.some((ajout) => ajout.referenceFne.toLowerCase() === cle(ligne)),
  );
  const nouvelles = ajouts
    .filter((ajout) => ajout.referenceSage.trim() !== "")
    .map((ajout) => `${ajout.referenceFne};${ajout.referenceSage.trim()}`);

  return [...conserves, ...nouvelles].join("\n");
}

/**
 * Ajoute des correspondances a la table CSV existante, en remplacant celles
 * qui portent deja le meme identifiant.
 */
export function fusionnerClients(
  csv: string,
  ajouts: Array<{ ncc: string; nom: string; compte: string }>,
): string {
  const lignes = csv.split(/\r?\n/).filter((ligne) => ligne.trim() !== "");
  const identifiant = (ligne: string) => (ligne.split(/[;,\t]/)[0] ?? "").trim().toLowerCase();

  const conserves = lignes.filter((ligne) => {
    const cle = identifiant(ligne);
    return !ajouts.some((ajout) => (ajout.ncc || ajout.nom).toLowerCase() === cle);
  });

  const nouvelles = ajouts
    .filter((ajout) => ajout.compte.trim() !== "")
    .map((ajout) => `${ajout.ncc};${ajout.nom};${ajout.compte.trim()}`);

  return [...conserves, ...nouvelles].join("\n");
}
