import { Facture, totalLignes } from "./modele";
import { ClientInconnu } from "./comptes";
import { arrondir } from "./texte";

/**
 * Ce qui doit etre vu avant l'import, pas apres le rejet de Sage.
 *
 * Chaque controle correspond a un refus deja rencontre ou a une donnee
 * fausse qui passerait inapercue. Rien d'autre : un avertissement qu'on
 * apprend a ignorer ne protege de rien.
 */

export type Gravite = "erreur" | "avertissement";

export interface Anomalie {
  gravite: Gravite;
  code: string;
  message: string;
}

export function controler(
  factures: Facture[],
  clientsInconnus: ClientInconnu[],
  unitesInconnues: string[],
  compteParDefaut = "",
): Anomalie[] {
  const anomalies: Anomalie[] = [];

  for (const client of clientsInconnus) {
    const identite =
      `"${client.nom || client.ncc}"` + (client.ncc ? ` (NCC ${client.ncc})` : "");
    // Un compte par defaut rend la piece importable, mais elle atterrit sur un
    // compte collectif : c'est une reserve, pas un blocage.
    anomalies.push(
      compteParDefaut
        ? {
            gravite: "avertissement",
            code: "COMPTE_PAR_DEFAUT",
            message:
              `${client.factures} facture(s) de ${identite} partent sur ${compteParDefaut}, ` +
              "faute de compte tiers propre.",
          }
        : {
            gravite: "erreur",
            code: "COMPTE_TIERS_MANQUANT",
            message:
              `Aucun compte tiers Sage pour ${identite} : ${client.factures} facture(s). ` +
              "Completez la table des comptes tiers.",
          },
    );
  }

  for (const facture of factures) {
    const piece = facture.reference || `facture ${facture.rang} du fichier`;

    if (!facture.date) {
      anomalies.push({
        gravite: "erreur",
        code: "DATE_ABSENTE",
        message: `${piece} : sans date, Sage refuse la piece (zones 2 et 6 vides).`,
      });
    }

    // Sage recalcule le HT depuis prix x quantite : un ecart avec le total
    // certifie par FNE se retrouverait dans la comptabilite.
    const ecart = arrondir(totalLignes(facture) - facture.totalHT, 2);
    if (facture.totalHT !== 0 && Math.abs(ecart) > 1) {
      anomalies.push({
        gravite: "erreur",
        code: "ECART_TOTAL",
        message:
          `${piece} : la somme des lignes (${totalLignes(facture)}) s'ecarte de ${ecart} du ` +
          `total HT certifie (${facture.totalHT}). La lecture du fichier est incomplete.`,
      });
    }

    for (const ligne of facture.lignes) {
      if (ligne.reference === "") {
        anomalies.push({
          gravite: "erreur",
          code: "ARTICLE_SANS_REFERENCE",
          message: `${piece} : une ligne sans reference d'article ("${ligne.designation}").`,
        });
      }
      // Un compte tiers saisi dans la colonne des articles : Sage chercherait
      // un article de ce nom et refuserait la ligne.
      if (/^4[01]1/.test(ligne.reference)) {
        anomalies.push({
          gravite: "erreur",
          code: "ARTICLE_EST_UN_COMPTE",
          message:
            `${piece} : la reference d'article "${ligne.reference}" est un compte tiers. ` +
            "Les comptes en 401 et 411 designent des fournisseurs et des clients.",
        });
      }
    }
  }

  if (unitesInconnues.length > 0) {
    anomalies.push({
      gravite: "avertissement",
      code: "UNITE_INCONNUE",
      message:
        `Unite(s) sans correspondance : ${unitesInconnues.join(", ")}. ` +
        "Elles partent telles quelles ; ajoutez-les a la table des unites si Sage les refuse.",
    });
  }

  const autresTaxes = factures.filter((facture) => facture.totalAutresTaxes !== 0);
  if (autresTaxes.length > 0) {
    const total = arrondir(
      autresTaxes.reduce((somme, facture) => somme + facture.totalAutresTaxes, 0),
      2,
    );
    anomalies.push({
      gravite: "avertissement",
      code: "AUTRES_TAXES",
      message:
        `${autresTaxes.length} facture(s) portent un prelevement en plus de la TVA, ` +
        `${total} au total. Il est ecrit ligne a ligne (code AIRSI), mais le format n'ayant ` +
        "qu'une zone de taxe, une ligne soumise a la fois a la TVA et au prelevement ne " +
        "transporte que la TVA.",
    });
  }

  return anomalies;
}

export function resume(factures: Facture[]) {
  const avoirs = factures.filter((facture) => facture.nature === "AVOIR").length;
  return {
    factures: factures.length - avoirs,
    avoirs,
    lignes: factures.reduce((somme, facture) => somme + facture.lignes.length, 0),
    totalHT: arrondir(
      factures.reduce((somme, facture) => somme + facture.totalHT, 0),
      2,
    ),
    totalTva: arrondir(
      factures.reduce((somme, facture) => somme + facture.totalTva, 0),
      2,
    ),
  };
}
