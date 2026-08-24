/** Outils de lecture communs aux differentes sources. */

/** Espaces insecables compris, ceux que les PDF glissent dans les nombres. */
const ESPACES = /[\s   ]+/g;

export function nettoyerLigne(valeur: string): string {
  return valeur.replace(ESPACES, " ").trim();
}

/**
 * Lit un nombre tel qu'une facture l'affiche : "15 600 000", "1 077",
 * "1.5", "4 594 500,50". Retourne null si ce n'en est pas un.
 */
export function nombre(valeur: string): number | null {
  const propre = valeur.replace(/\*\*/g, "").replace(ESPACES, "").replace(/−/g, "-");
  if (propre === "" || !/[0-9]/.test(propre)) return null;
  // Virgule decimale des factures francophones ; le point sert aussi de
  // decimale dans les taux ("1.5"), jamais de separateur de milliers ici,
  // les milliers etant separes par des espaces.
  const normalise = propre.replace(",", ".");
  if (!/^-?\d+(\.\d+)?$/.test(normalise)) return null;
  return Number(normalise);
}

/** Arrondit a n decimales sans laisser trainer les artefacts du binaire. */
export function arrondir(valeur: number, decimales: number): number {
  const facteur = 10 ** decimales;
  return Math.round((valeur + Number.EPSILON) * facteur) / facteur;
}

/**
 * Recolle deux fragments qu'un retour a la ligne a separes dans le PDF.
 *
 * Le trait d'union final est celui de la coupure : "MACKER-" et "EL" donnent
 * "MACKEREL". Ailleurs, un espace suffit : "FOIE DE BOEUF AL" et "TAMAM".
 */
export function recoller(debut: string, suite: string): string {
  if (debut === "") return suite;
  if (suite === "") return debut;
  if (debut.endsWith("-")) return debut.slice(0, -1) + suite;
  return `${debut} ${suite}`;
}

/** Date jj/mm/aaaa vers ISO aaaa-mm-jj. */
export function dateIso(valeur: string): string {
  const trouve = valeur.match(/(\d{2})\/(\d{2})\/(\d{4})/);
  if (!trouve) return "";
  return `${trouve[3]}-${trouve[2]}-${trouve[1]}`;
}
