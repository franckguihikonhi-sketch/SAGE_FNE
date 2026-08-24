import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { Facture } from "@/modele";
import { decimal, ecrireSage, jjmmaa, REGLAGES_PAR_DEFAUT } from "@/ecrire-sage";

/**
 * Aller-retour sur un exemplaire du format que le dossier importe.
 *
 * Le fichier est relu, ses documents reconstruits, puis reecrits : toute
 * difference serait un ecart de notre ecriture, puisque les donnees sont les
 * memes. C'est le seul controle qui prouve que le fichier produit est bien
 * celui que Sage accepte.
 *
 * Le contenu est anonymise, la structure est celle relevee sur l'exemplaire
 * reel : prelevement AIRSI a 1,5 %, ligne exoneree, taux reduit, taux normal,
 * document a deux lignes.
 */
const ATTENDU = new URL("./fixtures/sage-attendu.txt", import.meta.url);

function relire(contenu: string): Facture[] {
  const documents = new Map<string, Facture>();
  const ordre: string[] = [];
  const nombre = (valeur: string) => Number(valeur.replace(",", ".") || "0");

  for (const enregistrement of contenu.split("\r\n").filter((ligne) => ligne !== "")) {
    const z = enregistrement.split("\t");
    const cle = z[4]!;
    let facture = documents.get(cle);
    if (!facture) {
      facture = {
        reference: cle,
        date: `20${z[1]!.slice(4, 6)}-${z[1]!.slice(2, 4)}-${z[1]!.slice(0, 2)}`,
        nature: "FACTURE",
        client: { nom: "", ncc: "", compte: z[6]! },
        lignes: [],
        totalHT: 0,
        totalTva: 0,
        totalAutresTaxes: 0,
        rang: ordre.length + 1,
      };
      documents.set(cle, facture);
      ordre.push(cle);
    }
    facture.lignes.push({
      reference: z[7]!,
      designation: z[8]!,
      unite: z[11]!,
      quantite: nombre(z[10]!),
      prixUnitaire: nombre(z[9]!),
      montantHT: 0,
      codeTaxe: z[12]!,
      taux: nombre(z[13]!),
    });
  }

  return ordre.map((cle) => documents.get(cle)!);
}

describe("fichier d'import Sage", () => {
  it("reecrit l'exemplaire octet pour octet", () => {
    const attendu = readFileSync(ATTENDU);
    const factures = relire(attendu.toString("latin1"));

    const produit = ecrireSage(factures, {
      ...REGLAGES_PAR_DEFAUT,
      depot: "DEPÔT PRINCIPAL",
      numeroPiece: "reference",
    });

    expect(Buffer.from(produit.octets).equals(attendu)).toBe(true);
    expect(produit.enregistrements).toBe(5);
  });

  it("n'ecrit ni ligne d'entete ni ligne de cloture", () => {
    // Sur les soixante enregistrements de l'exemplaire reel, cinquante-sept
    // documents ne portent aucune ligne sans article : un enregistrement de
    // plus serait un rejet de plus.
    const factures = relire(readFileSync(ATTENDU).toString("latin1"));
    const produit = ecrireSage(factures, REGLAGES_PAR_DEFAUT);
    const zones = produit.texte.split("\r\n").filter(Boolean).map((ligne) => ligne.split("\t"));

    expect(zones).toHaveLength(5);
    expect(zones.every((ligne) => ligne[7] !== "" && ligne[8] !== "")).toBe(true);
  });

  it("marque l'avoir par le type de document", () => {
    const [facture] = relire(readFileSync(ATTENDU).toString("latin1"));
    const avoir = { ...facture!, nature: "AVOIR" as const };
    const zones = ecrireSage([avoir], REGLAGES_PAR_DEFAUT).texte.split("\t");

    expect(zones[3]).toBe("5");
  });

  it("laisse Sage numeroter par defaut", () => {
    const factures = relire(readFileSync(ATTENDU).toString("latin1"));
    const zones = ecrireSage(factures, REGLAGES_PAR_DEFAUT).texte.split("\r\n")[0]!.split("\t");

    expect(zones[4]).toBe("");
    expect(zones).toHaveLength(14);
  });

  it("ecrit les dates et les nombres a la forme du dossier", () => {
    expect(jjmmaa("2026-08-20")).toBe("200826");
    expect(jjmmaa("")).toBe("");
    expect(decimal(1077.2763, 6)).toBe("1077,276300");
    expect(decimal(20, 4)).toBe("20,0000");
  });
});
