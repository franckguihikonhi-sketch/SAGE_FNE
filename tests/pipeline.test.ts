import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { convertFichier as convert } from "@/lib/node/convert";
import { detectMapping } from "@/lib/fne/mapping";
import { readCsv } from "@/lib/fne/read";

const FIXTURE = new URL("./fixtures/export-fne-exemple.csv", import.meta.url);
const buffer = () => readFileSync(FIXTURE);

describe("detection des colonnes", () => {
  it("reconnait les colonnes d'un export FNE", () => {
    const table = readCsv(buffer());
    const { mapping } = detectMapping(table.columns);

    expect(mapping.numeroFacture).toBe("Numéro de facture");
    expect(mapping.dateFacture).toBe("Date de facture");
    expect(mapping.clientNom).toBe("Nom du client");
    expect(mapping.clientNcc).toBe("NCC client");
    expect(mapping.articleReference).toBe("Référence article");
    expect(mapping.quantite).toBe("Quantité");
    expect(mapping.montantHT).toBe("Montant HT");
    expect(mapping.totalTTC).toBe("Total TTC");
  });
});

describe("conversion complete", () => {
  it("regroupe les lignes par facture et calcule les totaux", async () => {
    const result = await convert(buffer(), "export-fne-exemple.csv");

    expect(result.invoices).toHaveLength(3);
    const premiere = result.invoices[0]!;
    expect(premiere.numero).toBe("FA-2026-0001");
    expect(premiere.date).toBe("2026-08-12");
    expect(premiere.lignes).toHaveLength(2);
    expect(premiere.client.nom).toBe("ETS KOUAME ET FILS");
    expect(premiere.client.ncc).toBe("1234567 A");
    expect(premiere.totaux.totalHT).toBe(700000);
    expect(premiere.totaux.totalTTC).toBe(826000);
    expect(premiere.lignes[0]!.tauxTva).toBe(18);
    expect(premiere.lignes[1]!.montantHT).toBe(200000);
  });

  it("identifie les avoirs", async () => {
    const result = await convert(buffer(), "export-fne-exemple.csv");
    const avoir = result.invoices.find((invoice) => invoice.numero === "AV-2026-0007");
    expect(avoir?.kind).toBe("AVOIR");
  });

  it("applique le taux reduit a partir du code taxe FNE", async () => {
    const result = await convert(buffer(), "export-fne-exemple.csv");
    const facture = result.invoices.find((invoice) => invoice.numero === "FA-2026-0002");
    expect(facture?.lignes[0]!.tauxTva).toBe(9);
    expect(facture?.lignes[0]!.remisePourcent).toBe(5);
  });

  it("signale les clients sans compte tiers Sage", async () => {
    const result = await convert(buffer(), "export-fne-exemple.csv");
    expect(result.clientsInconnus.map((client) => client.nom)).toContain("ETS KOUAME ET FILS");
    expect(result.issues.some((issue) => issue.code === "COMPTE_TIERS_MANQUANT")).toBe(true);
  });

  it("resout les comptes tiers via la table de correspondance", async () => {
    const result = await convert(buffer(), "export-fne-exemple.csv", {
      customers: [
        { ncc: "1234567 A", codeSage: "411KOUAME" },
        { nom: "SOCIETE IVOIRIENNE DE DISTRIBUTION", codeSage: "411SID" },
      ],
    });

    expect(result.clientsInconnus).toHaveLength(0);
    expect(result.issues.some((issue) => issue.code === "COMPTE_TIERS_MANQUANT")).toBe(false);
    expect(result.invoices[0]!.client.code).toBe("411KOUAME");
  });
});

describe("fichier d'import Sage", () => {
  it("produit un enregistrement d'entete puis les lignes", async () => {
    const result = await convert(buffer(), "export-fne-exemple.csv", {
      profileId: "sage100-documents-ventes",
      customers: [{ ncc: "1234567 A", codeSage: "411KOUAME" }],
    });

    const rows = result.file.content.split("\r\n").filter(Boolean);
    // 3 factures + 4 lignes d'article
    expect(rows).toHaveLength(7);

    const entete = rows[0]!.split("\t");
    expect(entete[0]).toBe("E");
    expect(entete[1]).toBe("6"); // DO_Type facture
    expect(entete[2]).toBe("FA-2026-0001");
    expect(entete[3]).toBe("12082026");
    expect(entete[4]).toBe("411KOUAME");

    const ligne = rows[1]!.split("\t");
    expect(ligne[0]).toBe("L");
    expect(ligne[1]).toBe("FA-2026-0001");
    expect(ligne[3]).toBe("ART-001");
    expect(ligne[9]).toBe("500000.00");
  });

  it("marque l'avoir avec le code document dedie", async () => {
    const result = await convert(buffer(), "export-fne-exemple.csv", {
      profileId: "sage100-documents-ventes",
    });
    const entete = result.file.content
      .split("\r\n")
      .find((row) => row.includes("AV-2026-0007"))!
      .split("\t");
    expect(entete[1]).toBe("5");
  });

  it("encode le fichier en Windows-1252", async () => {
    const result = await convert(buffer(), "export-fne-exemple.csv");
    const decoded = Buffer.from(result.file.base64, "base64");
    // "Fer a beton" contient un accent : en Windows-1252 l'octet vaut 0xE9.
    expect(decoded.includes(Buffer.from([0xe9]))).toBe(true);
  });

  it("genere un CSV de controle avec entetes", async () => {
    const result = await convert(buffer(), "export-fne-exemple.csv", {
      profileId: "sage100-csv-controle",
    });
    const rows = result.file.content.split("\r\n").filter(Boolean);
    expect(rows[0]!.startsWith("Type de document;Numero de piece")).toBe(true);
    expect(result.file.filename.endsWith(".csv")).toBe(true);
    expect(rows).toHaveLength(5); // entete + 4 lignes d'article
  });
});
