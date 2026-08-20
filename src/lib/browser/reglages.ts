/**
 * Reglages du poste, conserves d'une session a l'autre.
 *
 * Un comptable convertit ses factures chaque semaine avec le meme depot, la
 * meme souche et la meme table de correspondance clients : les ressaisir a
 * chaque fois est la principale friction de l'outil. Tout reste local au
 * navigateur, aucune donnee ne part sur un serveur.
 */

const CLE = "passerelle-fne-sage.reglages.v1";

export interface Reglages {
  profil: string;
  depot: string;
  souche: string;
  numeroPiece: string;
  compteDefaut: string;
  /** Table de correspondance clients, au format CSV `ncc;nom;compte`. */
  clients: string;
  /** Correspondance des modes de reglement, une ligne par code FNE. */
  reglements: string;
}

export const REGLAGES_PAR_DEFAUT: Reglages = {
  profil: "sage100-import-export",
  depot: "",
  souche: "1",
  numeroPiece: "sequence",
  compteDefaut: "",
  clients: "",
  reglements: "",
};

export function lireReglages(): Reglages {
  try {
    const brut = localStorage.getItem(CLE);
    if (!brut) return { ...REGLAGES_PAR_DEFAUT };
    const stocke = JSON.parse(brut) as Partial<Reglages>;
    // Fusion avec les valeurs par defaut : une version anterieure peut ne pas
    // porter tous les champs.
    return { ...REGLAGES_PAR_DEFAUT, ...stocke };
  } catch {
    return { ...REGLAGES_PAR_DEFAUT };
  }
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
