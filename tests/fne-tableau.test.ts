import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { convertFichier as convert } from "@/lib/node/convert";

const FIXTURE = new URL("./fixtures/fne-tableau.csv", import.meta.url);
const buffer = () => readFileSync(FIXTURE);

const SEQUENCE = { numeroPiece: "sequence" as const };

const ARTICLES_18_9 = [
  { taux: 18, article: "DIVERS18" },
  { taux: 9, article: "DIVERS9" },
  { taux: 0, article: "DIVERSEXO" },
];

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
    expect(result.unmappedColumns).toHaveLength(0);

    // Les colonnes sans usage comptable sont ecartees avant la detection.
    expect(result.source.colonnesEcartees).toContain("RCCM");
    expect(result.mapping.timbre).toBeUndefined();
  });

  it("ne retient que les colonnes demandees, et celles qui identifient la piece", async () => {
    const result = await convert(buffer(), "fne-tableau.csv", { customers: CLIENTS });

    expect(result.source.colonnesRetenues).toEqual(
      ["A", "C", "E", "F", "G", "I", "J", "K", "L", "N", "O", "P", "U"],
    );
    expect(result.source.columns).toHaveLength(13);
    expect(result.issues.some((issue) => issue.message.includes("Lecture restreinte"))).toBe(true);
  });

  it("annonce ce que coute l'abandon des colonnes d'identification", async () => {
    const result = await convert(buffer(), "fne-tableau.csv", {
      customers: CLIENTS,
      colonnes: { complement: false },
    });

    expect(result.source.colonnesRetenues).toEqual(["F", "I", "J", "K", "L", "N", "O", "P", "U"]);
    // Sans la colonne C, la piece n'a plus de reference : la cle technique de
    // regroupement ne doit pas ressortir dans le libelle envoye a Sage.
    expect(result.invoices[0]!.numeroFne).toBe("");
    expect(result.invoices[0]!.lignes[0]!.designation).toBe("Facture FNE");
    expect(
      result.issues.some((issue) => issue.message.includes("sans reference FNE")),
    ).toBe(true);
  });

  it("laisse un fichier d'une autre forme intact", async () => {
    // Les lettres designent des positions de l'export tableur FNE : un CSV
    // quelconque garde toutes ses colonnes.
    const autre = new URL("./fixtures/export-fne-exemple.csv", import.meta.url);
    const result = await convert(readFileSync(autre), "export-fne-exemple.csv", {});

    expect(result.source.colonnesEcartees).toHaveLength(0);
    expect(result.mapping.montantHT).toBeTruthy();
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

  it("reconstitue une facture a taux melange entre les deux taux qui l'encadrent", async () => {
    const result = await convert(buffer(), "fne-tableau.csv", {
      customers: CLIENTS,
      normalizeOptions: { ...SEQUENCE, articlesSynthese: ARTICLES_18_9 },
    });
    // 100 000 HT pour 13 770 de TVA : taux effectif de 13,77 %, entre 9 et 18.
    const melangee = result.invoices.find((invoice) => invoice.numero === "26000000870")!;

    expect(melangee.lignes).toHaveLength(2);
    // 100 000 x (13,77 - 9) / (18 - 9) = 53 000 au taux normal, le reste a 9 %.
    expect(melangee.lignes[0]!.montantHT).toBe(53000);
    expect(melangee.lignes[0]!.tauxTva).toBe(18);
    expect(melangee.lignes[0]!.referenceArticle).toBe("DIVERS18");
    expect(melangee.lignes[1]!.montantHT).toBe(47000);
    expect(melangee.lignes[1]!.tauxTva).toBe(9);
    expect(melangee.lignes[1]!.referenceArticle).toBe("DIVERS9");

    // Les totaux de la facture sont conserves par la reconstitution.
    const sommeHT = melangee.lignes.reduce((total, ligne) => total + ligne.montantHT, 0);
    const sommeTva = melangee.lignes.reduce((total, ligne) => total + ligne.montantTva, 0);
    expect(sommeHT).toBe(100000);
    expect(sommeTva).toBe(13770);

    // Reconstituer vaut mieux que bloquer : plus aucune anomalie de taux.
    expect(result.issues.some((issue) => issue.code === "TAUX_TVA_NON_CONFORME")).toBe(false);
  });

  it("partage entre taxable et exonere quand l'entreprise n'a qu'un taux", async () => {
    // Meme facture, meme taux effectif : c'est la liste des taux pratiques qui
    // decide de la decomposition.
    const result = await convert(buffer(), "fne-tableau.csv", {
      customers: CLIENTS,
      normalizeOptions: {
        ...SEQUENCE,
        articlesSynthese: [
          { taux: 18, article: "DIVERS18" },
          { taux: 0, article: "DIVERSEXO" },
        ],
      },
    });
    const melangee = result.invoices.find((invoice) => invoice.numero === "26000000870")!;

    // 13 770 / 18 % = 76 500 de part taxable, le reste exonere.
    expect(melangee.lignes.map((ligne) => [ligne.montantHT, ligne.tauxTva])).toEqual([
      [76500, 18],
      [23500, 0],
    ]);
  });

  it("porte chaque facture mono-taux sur l'article de son taux", async () => {
    const result = await convert(buffer(), "fne-tableau.csv", {
      customers: CLIENTS,
      normalizeOptions: { ...SEQUENCE, articlesSynthese: ARTICLES_18_9 },
    });
    const exoneree = result.invoices.find((invoice) => invoice.numero === "26000000863")!;
    const normale = result.invoices.find((invoice) => invoice.numero === "26000000890")!;

    // Un article par taux : sans cela, une facture a 9 % repartirait avec
    // l'article du taux normal et Sage lui appliquerait 18 %.
    expect(normale.lignes[0]!.referenceArticle).toBe("DIVERS18");
    expect(exoneree.lignes[0]!.referenceArticle).toBe("DIVERSEXO");
  });

  it("recapitule les reconstitutions plutot que de repeter un avertissement", async () => {
    const result = await convert(buffer(), "fne-tableau.csv", {
      customers: CLIENTS,
      normalizeOptions: { articlesSynthese: ARTICLES_18_9 },
    });

    expect(result.reconstitutions).toHaveLength(1);
    expect(result.reconstitutions[0]).toEqual({
      reference: "1234567A26000000870",
      tauxEffectif: 13.77,
      parts: [
        { taux: 18, ht: 53000, tva: 9540, article: "DIVERS18" },
        { taux: 9, ht: 47000, tva: 4230, article: "DIVERS9" },
      ],
      partBasse: 47,
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
