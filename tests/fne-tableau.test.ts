import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { convertFichier as convert } from "@/lib/node/convert";

const FIXTURE = new URL("./fixtures/fne-tableau.csv", import.meta.url);
const buffer = () => readFileSync(FIXTURE);

const SEQUENCE = { numeroPiece: "sequence" as const };

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
    // Une ligne par facture, sauf celle a plusieurs taux, reconstituee en deux.
    expect(result.invoices.map((invoice) => invoice.lignes.length)).toEqual([1, 1, 2, 1]);
    expect(result.invoices[0]!.lignes[0]!.designation).toBe("Facture FNE 1234567A26000000890");
    expect(result.invoices[0]!.lignes[0]!.tauxTva).toBe(18);
    expect(result.issues.some((issue) => issue.code === "LECTURE")).toBe(true);
  });

  it("laisse une facture mono-taux sur une seule ligne", async () => {
    const result = await convert(buffer(), "fne-tableau.csv", {
      customers: CLIENTS,
      normalizeOptions: SEQUENCE,
    });
    const exoneree = result.invoices.find((invoice) => invoice.numero === "26000000863")!;
    const normale = result.invoices.find((invoice) => invoice.numero === "26000000890")!;

    expect(exoneree.lignes).toHaveLength(1);
    expect(exoneree.lignes[0]!.tauxTva).toBe(0);
    expect(normale.lignes).toHaveLength(1);
    expect(normale.lignes[0]!.tauxTva).toBe(18);
  });

  it("reconstitue une facture a plusieurs taux en part taxable et part exoneree", async () => {
    const result = await convert(buffer(), "fne-tableau.csv", {
      customers: CLIENTS,
      normalizeOptions: { ...SEQUENCE, articleSynthese: "DIVERS18", articleSyntheseExonere: "DIVERSEXO" },
    });
    // 100 000 HT pour 13 770 de TVA : taux effectif de 13,77 %, hors nomenclature.
    const melangee = result.invoices.find((invoice) => invoice.numero === "26000000870")!;

    expect(melangee.lignes).toHaveLength(2);
    // La part taxable se deduit du total TVA : 13 770 / 18 % = 76 500.
    expect(melangee.lignes[0]!.montantHT).toBe(76500);
    expect(melangee.lignes[0]!.tauxTva).toBe(18);
    expect(melangee.lignes[0]!.referenceArticle).toBe("DIVERS18");
    expect(melangee.lignes[1]!.montantHT).toBe(23500);
    expect(melangee.lignes[1]!.tauxTva).toBe(0);
    expect(melangee.lignes[1]!.referenceArticle).toBe("DIVERSEXO");

    // Les totaux de la facture sont conserves par la reconstitution.
    const sommeHT = melangee.lignes.reduce((total, ligne) => total + ligne.montantHT, 0);
    const sommeTva = melangee.lignes.reduce((total, ligne) => total + ligne.montantTva, 0);
    expect(sommeHT).toBe(100000);
    expect(sommeTva).toBe(13770);

    // Reconstituer vaut mieux que bloquer : plus aucune anomalie de taux.
    expect(result.issues.some((issue) => issue.code === "TAUX_TVA_NON_CONFORME")).toBe(false);
  });

  it("recapitule les reconstitutions plutot que de repeter un avertissement", async () => {
    const result = await convert(buffer(), "fne-tableau.csv", {
      customers: CLIENTS,
      normalizeOptions: { articleSynthese: "DIVERS18", articleSyntheseExonere: "DIVERSEXO" },
    });

    expect(result.reconstitutions).toHaveLength(1);
    expect(result.reconstitutions[0]).toEqual({
      reference: "1234567A26000000870",
      tauxEffectif: 13.77,
      htTaxable: 76500,
      htExonere: 23500,
      partExoneree: 23.5,
    });

    // Le detail vit dans le tableau : aucun avertissement par facture.
    expect(
      result.issues.filter((issue) => issue.message.includes("melange plusieurs taux")),
    ).toHaveLength(0);
  });

  it("avertit quand les deux parts partagent le meme article", async () => {
    const result = await convert(buffer(), "fne-tableau.csv", { customers: CLIENTS });
    const issue = result.issues.find((entry) => entry.message.includes("meme regime de TVA"))!;

    // Sans article distinct, Sage donnerait le meme regime aux deux parts.
    expect(issue.severity).toBe("avertissement");
    expect(issue.message).toContain("1 facture(s)");
  });

  it("identifie l'avoir par le sous-type et retablit les montants positifs", async () => {
    const result = await convert(buffer(), "fne-tableau.csv", {
      customers: CLIENTS,
      normalizeOptions: SEQUENCE,
    });
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
