import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { convertFichier as convert } from "@/lib/node/convert";
import { isFneNativeExport, sequenceFromReference } from "@/lib/fne/native";

const FIXTURE = new URL("./fixtures/fne-natif.json", import.meta.url);
const buffer = () => readFileSync(FIXTURE);

const CLIENTS = [
  { ncc: "7654321B", codeSage: "411DEMO" },
  { ncc: "9988776C", codeSage: "411AUTRE" },
];

// Le defaut laisse la zone du numero de piece vide, comme les fichiers de
// reference du dossier client. Les controles qui portent sur ce numero
// demandent donc explicitement la numerotation par sequence.
const SEQUENCE = { normalizeOptions: { numeroPiece: "sequence" as const } };

describe("reconnaissance de l'export natif", () => {
  it("distingue un export FNE d'un tableau JSON quelconque", () => {
    expect(isFneNativeExport(JSON.parse(buffer().toString()))).toBe(true);
    expect(isFneNativeExport([{ colonne: "valeur" }])).toBe(false);
    expect(isFneNativeExport({ data: [{ reference: "X", totalAfterTaxes: 1 }] })).toBe(true);
  });

  it("extrait la partie annee + numero de la reference FNE", () => {
    expect(sequenceFromReference("2304903U26000000889")).toBe("26000000889");
    expect(sequenceFromReference("A2304903U2600000038")).toBe("A2600000038");
    expect(sequenceFromReference("FA-2026-0001")).toBe("FA-2026-0001");
  });
});

describe("lecture de l'export natif FNE", () => {
  it("lit les articles, les unites et les taux de chaque ligne", async () => {
    const result = await convert(buffer(), "fne-natif.json", { customers: CLIENTS, ...SEQUENCE });

    expect(result.source.kind).toBe("fne-json");
    expect(result.invoices).toHaveLength(2);

    const facture = result.invoices[0]!;
    expect(facture.numero).toBe("26000000123");
    expect(facture.numeroFne).toBe("1234567A26000000123");
    expect(facture.kind).toBe("FACTURE");
    expect(facture.client.nom).toBe("CLIENT DE DEMONSTRATION");
    expect(facture.client.code).toBe("411DEMO");
    expect(facture.modeReglement).toBe("deferred");
    expect(facture.template).toBe("B2B");
    expect(facture.lignes).toHaveLength(2);

    const premiere = facture.lignes[0]!;
    // `amount` de FNE est le prix unitaire HT, pas le total de la ligne.
    expect(premiere.prixUnitaireHT).toBe(1077.2763);
    expect(premiere.quantite).toBe(20);
    expect(premiere.montantHT).toBe(21545.53);
    expect(premiere.tauxTva).toBe(18);
    expect(premiere.codeTaxeFne).toBe("TVA");
    expect(premiere.unite).toBe("SAC");

    const seconde = facture.lignes[1]!;
    expect(seconde.tauxTva).toBe(0);
    expect(seconde.codeTaxeFne).toBe("TVAC");
    expect(seconde.montantHT).toBe(2000);
    expect(seconde.montantTva).toBe(0);
  });

  it("reprend les totaux declares par FNE", async () => {
    const result = await convert(buffer(), "fne-natif.json", { customers: CLIENTS });
    const facture = result.invoices[0]!;
    expect(facture.totaux.totalHT).toBe(23545.53);
    expect(facture.totaux.totalTva).toBe(3878.19);
    expect(facture.totaux.totalTTC).toBe(27423.72);
    expect(facture.totaux.netAPayer).toBe(27423.72);
  });

  it("ramene les avoirs en valeurs positives et conserve la facture d'origine", async () => {
    const result = await convert(buffer(), "fne-natif.json", { customers: CLIENTS, ...SEQUENCE });
    const avoir = result.invoices[1]!;

    expect(avoir.kind).toBe("AVOIR");
    expect(avoir.numero).toBe("A2600000004");
    expect(avoir.numeroParent).toBe("1234567A26000000099");
    expect(avoir.totaux.totalHT).toBe(21545.53);
    expect(avoir.lignes[0]!.prixUnitaireHT).toBe(1077.2763);
    expect(avoir.lignes[0]!.montantHT).toBe(21545.53);
  });

  it("conserve les montants negatifs quand l'option est desactivee", async () => {
    const result = await convert(buffer(), "fne-natif.json", {
      customers: CLIENTS,
      normalizeOptions: { avoirEnValeurAbsolue: false },
    });
    const avoir = result.invoices[1]!;
    expect(avoir.kind).toBe("AVOIR");
    expect(avoir.totaux.totalHT).toBe(-21545.53);
  });

  it("garde la reference FNE complete comme numero de piece si demande", async () => {
    const result = await convert(buffer(), "fne-natif.json", {
      customers: CLIENTS,
      normalizeOptions: { numeroPiece: "reference" },
    });
    expect(result.invoices[0]!.numero).toBe("1234567A26000000123");
    expect(result.issues.some((issue) => issue.code === "PIECE_TROP_LONGUE")).toBe(true);
  });

  it("ecrit le code reglement Sage a partir du mode de paiement FNE", async () => {
    const result = await convert(buffer(), "fne-natif.json", {
      // Le profil du dossier client ne porte pas de zone reglement.
      profileId: "sage100-documents-ventes",
      customers: CLIENTS,
      reglements: { deferred: "CRED", cash: "ESP" },
    });
    const rows = result.file.content.split("\r\n").filter(Boolean);
    expect(rows[0]!.split("\t")[7]).toBe("CRED");
    expect(rows[3]!.split("\t")[7]).toBe("ESP");
  });

  it("ne signale aucun ecart de totaux sur un export natif", async () => {
    const result = await convert(buffer(), "fne-natif.json", { customers: CLIENTS });
    const codes = result.issues.map((issue) => issue.code);
    expect(codes).not.toContain("ECART_TOTAL_HT");
    expect(codes).not.toContain("ECART_TOTAL_TVA");
    expect(codes).not.toContain("TAUX_TVA_NON_CONFORME");
  });
});

describe("PDF de facture certifiee", () => {
  it("est refuse avec l'explication du refus", async () => {
    // Un PDF minimal suffit : le refus se joue sur l'extension, avant toute lecture.
    const pdf = Buffer.from("%PDF-1.7\n", "latin1");
    await expect(convert(pdf, "2304903U26000000889_20260811.pdf")).rejects.toThrow(
      /montants y sont arrondis au franc/,
    );
  });
});

describe("correspondance des articles", () => {
  it("traduit la reference FNE en reference Sage", async () => {
    const result = await convert(buffer(), "fne-natif.json", {
      customers: CLIENTS,
      articles: [{ referenceFne: "ART-001", referenceSage: "1147005" }],
    });

    expect(result.invoices[0]!.lignes[0]!.referenceArticle).toBe("1147005");
    // ART-002 n'a pas de correspondance : signale, et transmis tel quel.
    expect(result.invoices[0]!.lignes[1]!.referenceArticle).toBe("ART-002");
    expect(result.articlesInconnus.map((article) => article.referenceFne)).toEqual(["ART-002"]);
  });

  it("compte les lignes concernees par chaque article inconnu", async () => {
    const result = await convert(buffer(), "fne-natif.json", { customers: CLIENTS });
    const frites = result.articlesInconnus.find((a) => a.referenceFne === "ART-001")!;

    // L'article figure sur la facture et sur l'avoir.
    expect(frites.lignes).toBe(2);
    expect(frites.designation).toBe("FRITES 7MM-PK (4*2.5kg)");
  });
});
