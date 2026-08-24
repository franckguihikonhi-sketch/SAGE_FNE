import { Correspondance, lireTable, rapprocher, UNITES_PAR_DEFAUT, ClientInconnu } from "./comptes";
import { Anomalie, controler, resume } from "./controles";
import { decodeText } from "./cp1252";
import { ecrireSage, FichierSage, Reglages, REGLAGES_PAR_DEFAUT } from "./ecrire-sage";
import { estTexteDeFactures, lireFactures } from "./lire-factures";
import { estExportJson, lireJson } from "./lire-json";
import { Facture } from "./modele";

/**
 * De la facture certifiee au fichier d'import : une seule fonction, sans etat.
 *
 * Tout se passe sur le poste. Aucun fichier ne part sur un serveur, et il n'y
 * a rien a configurer au-dela des deux tables de correspondance.
 */

export interface OptionsConversion {
  reglages?: Partial<Reglages>;
  /** Table "NCC ou nom du client;compte tiers Sage". */
  clients?: string;
  /** Table "unite de la facture;unite Sage". */
  unites?: string;
  /** Compte applique aux clients sans correspondance, quand il est renseigne. */
  compteParDefaut?: string;
}

export interface Resultat {
  source: "pdf" | "json";
  factures: Facture[];
  clientsInconnus: ClientInconnu[];
  anomalies: Anomalie[];
  resume: ReturnType<typeof resume>;
  fichier: FichierSage;
}

export class ErreurLecture extends Error {}

export function convertir(
  contenu: Uint8Array | string,
  nomFichier: string,
  options: OptionsConversion = {},
): Resultat {
  const texte = typeof contenu === "string" ? contenu : decodeText(contenu);
  const reglages: Reglages = { ...REGLAGES_PAR_DEFAUT, ...options.reglages };

  const { source, factures, avertissements } = lire(texte, nomFichier);

  const clients: Correspondance[] = lireTable(options.clients ?? "");
  const unites: Correspondance[] = lireTable(options.unites ?? UNITES_PAR_DEFAUT);
  const rapproche = rapprocher(factures, clients, unites, options.compteParDefaut ?? "");

  const anomalies: Anomalie[] = [
    ...avertissements.map((message) => ({
      gravite: "avertissement" as const,
      code: "LECTURE",
      message,
    })),
    ...controler(
      rapproche.factures,
      rapproche.clientsInconnus,
      rapproche.unitesInconnues,
      options.compteParDefaut ?? "",
    ),
  ];

  const base = nomFichier.replace(/\.[^.]+$/, "") || "import";
  return {
    source,
    factures: rapproche.factures,
    clientsInconnus: rapproche.clientsInconnus,
    anomalies,
    resume: resume(rapproche.factures),
    fichier: ecrireSage(rapproche.factures, reglages, `${base}-sage`),
  };
}

function lire(texte: string, nomFichier: string) {
  if (estExportJson(texte)) {
    return { source: "json" as const, ...lireJson(texte) };
  }
  if (estTexteDeFactures(texte)) {
    return { source: "pdf" as const, ...lireFactures(texte) };
  }
  throw new ErreurLecture(
    `${nomFichier} n'est ni un export JSON de FNE, ni le texte de factures FNE. ` +
      "Attendu : les factures certifiees, ou l'export JSON de la plateforme.",
  );
}
