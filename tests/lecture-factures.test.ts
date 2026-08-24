import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { choisirTaxe, lireFactures, normaliserUnite } from "@/lire-factures";
import { totalLignes } from "@/modele";

const FIXTURE = new URL("./fixtures/factures-fne.md", import.meta.url);
const texte = () => readFileSync(FIXTURE, "utf8");
const lecture = () => lireFactures(texte());

describe("lecture des factures certifiees", () => {
  it("retrouve une facture par date, et son detail", () => {
    const { factures } = lecture();

    expect(factures).toHaveLength(3);
    expect(factures.map((facture) => facture.lignes.length)).toEqual([3, 1, 1]);
    expect(factures[0]!.date).toBe("2026-08-20");
    expect(factures[0]!.client.nom).toBe("CLIENT SANS NCC");
    expect(factures[1]!.client.ncc).toBe("7654321B");
  });

  it("recolle les cellules que le PDF a coupees en deux", () => {
    const ligne = lecture().factures[0]!.lignes[2]!;

    // "FROZEN JACK MACKER-" puis "EL" sur la ligne suivante ; l'unite "Car-"
    // puis "tons" ; la taxe "TVAD (0)," puis "AIRSI (1.5)".
    expect(ligne.designation).toBe("FROZEN JACK MACKEREL");
    expect(ligne.unite).toBe("CARTONS");
    expect(ligne.codeTaxe).toBe("AIRSI");
  });

  it("retablit le prix unitaire que le PDF a arrondi", () => {
    const ligne = lecture().factures[1]!.lignes[0]!;

    // La facture affiche 1 077 pour 120 sacs et 129 273 HT : le prix imprime
    // donnerait 129 240 dans Sage. Le montant certifie, lui, fait foi.
    expect(ligne.prixUnitaire).toBe(1077.275);
    expect(ligne.quantite * ligne.prixUnitaire).toBeCloseTo(129273, 6);
  });

  it("garde les totaux certifies et les retrouve dans les lignes", () => {
    for (const facture of lecture().factures) {
      expect(totalLignes(facture)).toBe(facture.totalHT);
    }
    expect(lecture().factures[0]!.totalAutresTaxes).toBe(340028);
    expect(lecture().factures[1]!.totalTva).toBe(23269);
  });

  it("ne prend pas le resume des taxes pour du detail", () => {
    // Le tableau "RESUME DE LA FACTURE" a la meme forme que celui des
    // articles et le suit immediatement.
    const references = lecture().factures[0]!.lignes.map((ligne) => ligne.reference);
    expect(references).toEqual(["ART-100", "ART-200", "ART-300"]);
  });

  it("reconnait l'avoir a son intitule", () => {
    const avoir = lecture().factures[2]!;

    expect(avoir.nature).toBe("AVOIR");
    expect(avoir.reference).toBe("A1234567A2600000012");
  });

  it("signale les factures dont le numero n'a pas ete imprime", () => {
    const { avertissements } = lecture();

    // La conversion du PDF perd l'entete de certaines factures : Sage
    // numerotera, mais il faut le savoir.
    expect(avertissements.some((message) => message.includes("numero de facture absent"))).toBe(true);
  });

  it("reconnait le prelevement malgre une lettre de travers", () => {
    // L'extraction du PDF ecrit parfois "MAEIRSI" pour "AIRSI".
    expect(choisirTaxe("TVAD (0), MAEIRSI (1.5)")).toEqual({ code: "AIRSI", taux: 1.5 });
    // La TVA l'emporte : la zone de taxe du format est unique.
    expect(choisirTaxe("TVA (18), AIRSI (1.5)")).toEqual({ code: "TVA", taux: 18 });
    expect(choisirTaxe("TVAD (0)")).toEqual({ code: "", taux: 0 });
  });

  it("nettoie les unites coupees", () => {
    expect(normaliserUnite("CAR-TON")).toBe("CARTON");
    expect(normaliserUnite("Car-tons")).toBe("CARTONS");
    expect(normaliserUnite("KILO-GRAM")).toBe("KILOGRAM");
  });
});
