import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { convertFichier as convert } from "@/lib/node/convert";

const FIXTURE = new URL("./fixtures/fne-tableau.csv", import.meta.url);
const buffer = () => readFileSync(FIXTURE);

const CLIENTS = [
  { ncc: "7654321B", codeSage: "411DEMO" },
  { ncc: "9988776C", codeSage: "411AUTRE" },
  { ncc: "2114866J", codeSage: "411TROIS" },
];

describe("export tableur FNE (entetes seuls)", () => {
  it("reconnait les colonnes reelles de l'export", async () => {
    const result = await convert(buffer(), "fne-tableau.csv", { customers: CLIENTS });

    expect(result.source.kind).toBe("tableau");
    expect(result.mapping.numeroFacture).toBe("Référence");
    expect(result.mapping.dateFacture).toBe("Date");
    expect(result.mapping.sousTypeDocument).toBe("Sous-type de facture");
    expect(result.mapping.clientNcc).toBe("NCC du client");
    expect(result.mapping.clientNom).toBe("Nom de la société / du client");
    expect(result.mapping.totalHT).toBe("Total HT");
    expect(result.mapping.netAPayer).toBe("Net a payer");
    expect(result.mapping.timbre).toBe("Timbre de quittance");
    expect(result.unmappedColumns).toHaveLength(0);
    expect(result.ignoredColumns).toContain("RCCM");
  });

  it("genere une ligne de synthese par facture et le signale", async () => {
    const result = await convert(buffer(), "fne-tableau.csv", { customers: CLIENTS });

    expect(result.source.synthese).toBe(true);
    expect(result.invoices).toHaveLength(4);
    expect(result.invoices.every((invoice) => invoice.lignes.length === 1)).toBe(true);
    expect(result.invoices[0]!.lignes[0]!.designation).toBe("Facture FNE 1234567A26000000890");
    expect(result.invoices[0]!.lignes[0]!.tauxTva).toBe(18);
    expect(result.issues.some((issue) => issue.code === "LECTURE")).toBe(true);
  });

  it("accepte un taux reconstitue a 0 % sur une facture exoneree", async () => {
    const result = await convert(buffer(), "fne-tableau.csv", { customers: CLIENTS });
    const exoneree = result.invoices.find((invoice) => invoice.numero === "26000000863")!;
    expect(exoneree.lignes[0]!.tauxTva).toBe(0);
    expect(
      result.issues.some(
        (issue) =>
          issue.code === "TAUX_TVA_NON_CONFORME" && issue.facture === "1234567A26000000863",
      ),
    ).toBe(false);
  });

  it("bloque les factures a plusieurs taux, que la synthese ne sait pas reconstituer", async () => {
    const result = await convert(buffer(), "fne-tableau.csv", { customers: CLIENTS });
    const issue = result.issues.find((entry) => entry.code === "TAUX_TVA_NON_CONFORME");

    expect(issue?.severity).toBe("erreur");
    // Les anomalies designent la facture par sa reference FNE.
    expect(issue?.facture).toBe("1234567A26000000870");
    expect(issue?.message).toContain("13.77");
    expect(issue?.message).toContain("export JSON");
  });

  it("identifie l'avoir par le sous-type et retablit les montants positifs", async () => {
    const result = await convert(buffer(), "fne-tableau.csv", { customers: CLIENTS });
    const avoir = result.invoices.find((invoice) => invoice.kind === "AVOIR")!;

    expect(avoir.numero).toBe("A2600000038");
    expect(avoir.numeroParent).toBe("1234567A26000000524");
    expect(avoir.totaux.totalHT).toBe(21545.53);
    expect(avoir.totaux.totalTTC).toBe(25423.72);

    const ligne = result.file.content
      .split("\r\n")
      .find((row) => row.includes("A2600000038"))!
      .split("\t");
    // Zone 5 du format du dossier client : type de document, 5 = avoir.
    expect(ligne[4]).toBe("5");
  });
});
