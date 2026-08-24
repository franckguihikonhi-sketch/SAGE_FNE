import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { convertir, ErreurLecture } from "@/convertir";

const FACTURES = new URL("./fixtures/factures-fne.md", import.meta.url);
const JSON_FNE = new URL("./fixtures/fne-natif.json", import.meta.url);

const CLIENTS = ["7654321B;411DEMO", "CLIENT SANS NCC;411INFORMEL"].join("\n");
const convertirFactures = (options = {}) =>
  convertir(readFileSync(FACTURES, "utf8"), "factures.md", {
    reglages: { depot: "DEPÔT PRINCIPAL" },
    clients: CLIENTS,
    ...options,
  });

const zones = (texte: string) =>
  texte.split("\r\n").filter(Boolean).map((ligne) => ligne.split("\t"));

describe("conversion de bout en bout", () => {
  it("produit un enregistrement par ligne d'article", () => {
    const resultat = convertirFactures();
    const lignes = zones(resultat.fichier.texte);

    expect(resultat.source).toBe("pdf");
    expect(resultat.resume).toEqual({
      factures: 2,
      avoirs: 1,
      lignes: 5,
      totalHT: 22815009,
      totalTva: 26372,
    });
    expect(lignes).toHaveLength(5);
    expect(lignes.every((ligne) => ligne.length === 14)).toBe(true);
  });

  it("ecrit les zones dans l'ordre du dossier", () => {
    const [premiere] = zones(convertirFactures().fichier.texte);

    expect(premiere![0]).toBe("");
    expect(premiere![1]).toBe("200826");
    expect(premiere![2]).toBe("DEPÔT PRINCIPAL");
    expect(premiere![3]).toBe("6");
    expect(premiere![4]).toBe("");
    expect(premiere![5]).toBe("200826");
    expect(premiere![6]).toBe("411INFORMEL");
    expect(premiere![7]).toBe("ART-100");
    expect(premiere![9]).toBe("5200,000000");
    expect(premiere![10]).toBe("3000,0000");
    expect(premiere![11]).toBe("CN");
    expect(premiere![12]).toBe("AIRSI");
    expect(premiere![13]).toBe("1,5000");
  });

  it("traduit les unites de la facture vers celles du dossier", () => {
    const lignes = zones(convertirFactures().fichier.texte);

    // CAR-TON, KILO-GRAM et Car-tons deviennent CN, KG et CN.
    expect(lignes.map((ligne) => ligne[11])).toEqual(["CN", "KG", "CN", "SAC", "SAC"]);
  });

  it("reclame un compte tiers plutot que d'en inventer un", () => {
    const resultat = convertir(readFileSync(FACTURES, "utf8"), "factures.md", {});

    expect(resultat.clientsInconnus.map((client) => client.nom)).toEqual([
      "CLIENT AVEC NCC",
      "CLIENT SANS NCC",
    ]);
    expect(resultat.anomalies.some((a) => a.code === "COMPTE_TIERS_MANQUANT")).toBe(true);
  });

  it("signale le prelevement que le format ne peut pas cumuler", () => {
    const anomalie = convertirFactures().anomalies.find((a) => a.code === "AUTRES_TAXES")!;

    expect(anomalie.gravite).toBe("avertissement");
    expect(anomalie.message).toContain("340028");
  });

  it("ne signale rien d'autre quand tout est en place", () => {
    const erreurs = convertirFactures().anomalies.filter((a) => a.gravite === "erreur");
    expect(erreurs).toEqual([]);
  });

  it("lit aussi l'export JSON, qui porte les prix exacts", () => {
    const resultat = convertir(readFileSync(JSON_FNE, "utf8"), "export.json", {
      clients: "7654321B;411DEMO\n9988776C;411AUTRE",
      reglages: { depot: "D" },
    });

    expect(resultat.source).toBe("json");
    expect(zones(resultat.fichier.texte)[0]![9]).toBe("1077,276300");
  });

  it("refuse un fichier qui n'est ni l'un ni l'autre", () => {
    expect(() => convertir("nom;prenom\nx;y", "carnet.csv", {})).toThrow(ErreurLecture);
  });
});
