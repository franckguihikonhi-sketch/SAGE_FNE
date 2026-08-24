import { Facture, Ligne, Nature } from "./modele";
import { arrondir, dateIso, nettoyerLigne, nombre, recoller } from "./texte";

/**
 * Lecture des factures certifiees FNE, telles qu'elles sortent en PDF.
 *
 * Le PDF est la seule sortie que la plateforme donne en nombre ; c'est donc
 * elle qui alimente la passerelle. Le texte lui est extrait tel quel - d'un
 * PDF converti, ou du PDF directement - et ce module y retrouve les factures.
 *
 * Ce texte est abime par nature : une cellule coupee en fin de ligne se
 * retrouve sur deux lignes ("MACKER-" puis "EL"), une unite devient
 * "CAR-TON", et l'extraction se trompe parfois de lettre ("MAEIRSI" pour
 * "AIRSI"). La lecture est donc tolerante, et signale ce qu'elle n'a pas su
 * lire plutot que de le deviner.
 */

export interface LectureFactures {
  factures: Facture[];
  avertissements: string[];
}

/** Chaque facture porte exactement une ligne "Date et heure". */
const ANCRE = /Date et heure\s*:/g;
const REFERENCE = /Facture\s+(de\s+vente|d[’']avoir|d'avoir)\s+N[ºo°]?\s*([A-Z0-9]+)/gi;
const NOM = /^Nom\s*:\s*(.+)$/m;
const NCC = /NCC\s*:\s*([A-Z0-9]{4,})/;

export function estTexteDeFactures(texte: string): boolean {
  return /Date et heure\s*:/.test(texte) && /Montant HT/i.test(texte);
}

export function lireFactures(source: string): LectureFactures {
  const texte = normaliser(source);
  const avertissements: string[] = [];

  const ancres = [...texte.matchAll(ANCRE)].map((trouve) => trouve.index ?? 0);
  if (ancres.length === 0) {
    return { factures: [], avertissements: ["Aucune facture reconnue dans ce fichier."] };
  }

  const factures = ancres.map((debut, rang) => {
    const fin = ancres[rang + 1] ?? texte.length;
    // Le numero est imprime au-dessus de la date : il se cherche donc dans ce
    // qui precede, depuis la fin de la facture precedente.
    const entete = texte.slice(rang === 0 ? 0 : ancres[rang - 1]!, debut);
    return lireFacture(texte.slice(debut, fin), entete, rang + 1, avertissements);
  });

  return { factures, avertissements };
}

/** Retire l'echappement Markdown et uniformise les espaces. */
function normaliser(texte: string): string {
  return texte
    .replace(/\\([-*_.()[\]])/g, "$1")
    .replace(/ | | /g, " ")
    .replace(/\r\n?/g, "\n");
}

function lireFacture(
  corps: string,
  entete: string,
  rang: number,
  avertissements: string[],
): Facture {
  const trouves = [...entete.matchAll(REFERENCE)];
  const dernier = trouves.at(-1);
  const reference = dernier ? dernier[2]! : "";
  const nature: Nature = dernier && /avoir/i.test(dernier[1]!) ? "AVOIR" : "FACTURE";
  const situation = reference ? `Facture ${reference}` : `Facture ${rang} du fichier`;

  const date = dateIso(corps.slice(0, 200));
  if (!date) avertissements.push(`${situation} : date absente ou illisible.`);
  if (!reference) {
    avertissements.push(
      `${situation} : numero de facture absent du texte. Sage numerotera lui-meme la piece.`,
    );
  }

  const nom = nettoyerLigne(corps.match(NOM)?.[1] ?? "");
  if (!nom) avertissements.push(`${situation} : nom du client absent.`);

  const { lignes, totaux } = lireTableau(corps, situation, avertissements);
  if (lignes.length === 0) avertissements.push(`${situation} : aucune ligne d'article lue.`);

  return {
    reference,
    date,
    nature,
    client: { nom, ncc: corps.match(NCC)?.[1] ?? "", compte: "" },
    lignes,
    totalHT: totaux.ht,
    totalTva: totaux.tva,
    totalAutresTaxes: totaux.autres,
    rang,
  };
}

interface Totaux {
  ht: number;
  tva: number;
  autres: number;
}

const LIBELLES: Array<[RegExp, keyof Totaux]> = [
  [/^TOTAL HT$/i, "ht"],
  [/^TVA$/i, "tva"],
  [/^AUTRES TAXES$/i, "autres"],
];

function lireTableau(
  corps: string,
  situation: string,
  avertissements: string[],
): { lignes: Ligne[]; totaux: Totaux } {
  const brutes: string[][] = [];
  const totaux: Totaux = { ht: 0, tva: 0, autres: 0 };
  let dansLeTableau = false;
  // Les totaux ferment le tableau des articles. Ce qui suit - le resume par
  // categorie de taxe - a la meme forme mais n'est pas du detail.
  let articlesTermines = false;

  for (const ligne of corps.split("\n")) {
    if (!ligne.trimStart().startsWith("|")) continue;
    const cellules = decouper(ligne);
    if (cellules.every((cellule) => /^[:\-\s]*$/.test(cellule))) continue;

    if (/^R[ée]f$/i.test(cellules[0] ?? "")) {
      dansLeTableau = true;
      articlesTermines = false;
      continue;
    }
    if (!dansLeTableau) continue;

    const libelle = nettoyerLigne((cellules[2] ?? "").replace(/\*\*/g, ""));
    const total = LIBELLES.find(([motif]) => motif.test(libelle));
    if (total) {
      totaux[total[1]] = nombre(cellules.at(-1) ?? "") ?? 0;
      articlesTermines = true;
      continue;
    }
    // Les autres lignes de pied (TOTAL TTC, TIMBRE, TOTAL A PAYER) ne servent
    // pas a l'import : le format n'a pas de zone pour elles.
    if (/^(TOTAL|TIMBRE)/i.test(libelle)) {
      articlesTermines = true;
      continue;
    }
    if (articlesTermines) continue;

    if ((cellules[0] ?? "") !== "") brutes.push(cellules);
    else if (brutes.length > 0) {
      // Cellule coupee par un retour a la ligne du PDF : elle appartient a la
      // ligne d'article precedente.
      const precedente = brutes[brutes.length - 1]!;
      cellules.forEach((cellule, index) => {
        if (cellule !== "") precedente[index] = recoller(precedente[index] ?? "", cellule);
      });
    }
  }

  const lignes = brutes.map((cellules) => construireLigne(cellules, situation, avertissements));
  return { lignes, totaux };
}

function decouper(ligne: string): string[] {
  const propre = ligne.trim().replace(/^\|/, "").replace(/\|$/, "");
  return propre.split("|").map((cellule) => nettoyerLigne(cellule.replace(/\*\*/g, "")));
}

function construireLigne(
  cellules: string[],
  situation: string,
  avertissements: string[],
): Ligne {
  const reference = cellules[0] ?? "";
  const quantite = nombre(cellules[3] ?? "") ?? 0;
  const montantHT = nombre(cellules[7] ?? "") ?? 0;
  const affiche = nombre(cellules[2] ?? "") ?? 0;

  // Le PDF arrondit le prix unitaire au franc : 1 077 pour 1 077,2763. Le
  // montant HT, lui, est celui que FNE a certifie. Rededuire le prix du
  // montant rend a Sage un total identique a la facture, ce que le prix
  // imprime ne permettrait pas.
  const prixUnitaire = quantite !== 0 ? arrondir(montantHT / quantite, 6) : affiche;
  if (quantite === 0) {
    avertissements.push(`${situation} : quantite nulle ou illisible sur l'article ${reference}.`);
  }

  const { code, taux } = choisirTaxe(cellules[5] ?? "");
  return {
    reference,
    designation: cellules[1] ?? "",
    unite: normaliserUnite(cellules[4] ?? ""),
    quantite,
    prixUnitaire,
    montantHT,
    codeTaxe: code,
    taux,
  };
}

/**
 * La zone de taxe du format d'import n'en porte qu'une. Quand une ligne
 * declare une TVA et un prelevement, c'est la TVA qui compte ; une ligne
 * exoneree de TVA mais soumise a l'AIRSI part sous ce code, comme l'ecrit le
 * dossier lui-meme.
 */
export function choisirTaxe(cellule: string): { code: string; taux: number } {
  const taxes = [...cellule.matchAll(/([A-Za-z]+)\s*\(\s*([\d.,]+)\s*%?\s*\)/g)].map((trouve) => ({
    code: (trouve[1] ?? "").toUpperCase(),
    taux: nombre(trouve[2] ?? "") ?? 0,
  }));

  const tva = taxes.find((taxe) => taxe.code.startsWith("TVA") && taxe.taux > 0);
  if (tva) return { code: "TVA", taux: tva.taux };

  // "MAEIRSI" pour "AIRSI" : l'extraction du PDF perd ou ajoute une lettre.
  // La fin du mot suffit a le reconnaitre sans risquer une autre taxe.
  const prelevement = taxes.find((taxe) => /IRSI/.test(taxe.code));
  if (prelevement) return { code: "AIRSI", taux: prelevement.taux };

  const autre = taxes.find((taxe) => taxe.taux > 0);
  if (autre) return { code: autre.code, taux: autre.taux };

  return { code: "", taux: 0 };
}

/** "CAR-TON", "Car-tons", "KILO-GRAM" : la coupure du PDF traverse l'unite. */
export function normaliserUnite(valeur: string): string {
  return valeur.replace(/[^A-Za-z]/g, "").toUpperCase();
}
