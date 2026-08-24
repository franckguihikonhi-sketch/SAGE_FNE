import { readFileSync } from "node:fs";
import iconv from "iconv-lite";
import { describe, expect, it } from "vitest";
import { convertFichier as convert } from "@/lib/node/convert";
import { SAGE100_EXPORT_VERIFIE, SAGE100_IMPORT_EXPORT } from "@/lib/sage/profile";

const FIXTURE = new URL("./fixtures/fne-natif.json", import.meta.url);
const buffer = () => readFileSync(FIXTURE);
const CLIENTS = [
  { ncc: "7654321B", codeSage: "411DEMO" },
  { ncc: "9988776C", codeSage: "411AUTRE" },
];

const convertir = (options = {}) =>
  convert(buffer(), "fne-natif.json", {
    customers: CLIENTS,
    parametres: { depot: "DEPOT PRINCIPAL", souche: "1" },
    ...options,
  });

/** Le profil precedent, que Sage refuse a l'import : il sert de temoin. */
const ANCIEN = { profileId: "sage100-import-export" };

describe("profil de l'exemplaire verifie", () => {
  it("est le profil par defaut", async () => {
    const result = await convertir();
    expect(result.profile.id).toBe("sage100-export-verifie");
  });

  it("produit un fichier a plat de 14 zones tabulees", async () => {
    const result = await convertir();
    const decoded = iconv.decode(Buffer.from(result.file.base64, "base64"), "windows-1252");
    const rows = decoded.split("\r\n").filter(Boolean);

    // Format a plat : 3 lignes d'article et une ligne de cloture par document.
    expect(rows).toHaveLength(5);
    expect(rows.every((row) => row.split("\t").length === 14)).toBe(true);
    expect(decoded.endsWith("\r\n")).toBe(true);
  });

  it("ecrit les zones dans l'ordre releve sur l'exemplaire", async () => {
    const result = await convertir({ normalizeOptions: { numeroPiece: "sequence" } });
    const decoded = iconv.decode(Buffer.from(result.file.base64, "base64"), "windows-1252");
    const cells = decoded.split("\r\n")[0]!.split("\t");

    expect(cells[0]).toBe(""); // vide sur les soixante lignes de l'exemplaire
    expect(cells[1]).toBe("110826"); // date du document, jjmmaa
    expect(cells[2]).toBe("DEPOT PRINCIPAL");
    expect(cells[3]).toBe("6"); // type de document
    expect(cells[4]).toBe("26000000123"); // numero de piece
    expect(cells[5]).toBe("110826"); // date de livraison
    expect(cells[6]).toBe("411DEMO"); // compte tiers
    expect(cells[7]).toBe("ART-001");
    expect(cells[8]).toBe("FRITES 7MM-PK (4*2.5kg)");
    expect(cells[9]).toBe("1077,276300"); // prix unitaire, 6 decimales
    expect(cells[10]).toBe("20,0000"); // quantite, 4 decimales
    expect(cells[11]).toBe("SAC");
    expect(cells[12]).toBe("TVA"); // code taxe
    expect(cells[13]).toBe("18,0000"); // taux, 4 decimales
  });

  it("porte le taux de chaque ligne, y compris exonere", async () => {
    const result = await convertir();
    const rows = result.file.content
      .split("\r\n")
      .filter(Boolean)
      .map((row) => row.split("\t"));

    const exoneree = rows.find((row) => row[7] === "ART-002")!;
    // Sur l'exemplaire, une ligne exoneree porte un code taxe vide et un taux nul.
    expect(exoneree[12]).toBe("");
    expect(exoneree[13]).toBe("0,0000");
  });

  it("clot chaque document par la ligne relevee sur l'exemplaire", async () => {
    const rows = (await convertir()).file.content
      .split("\r\n")
      .filter(Boolean)
      .map((row) => row.split("\t"));

    const cloture = rows[2]!;
    expect(cloture[0]).toBe("");
    expect(cloture[1]).toBe(""); // date du document
    expect(cloture[2]).toBe(""); // depot
    expect(cloture[3]).toBe("6"); // type de document, comme ses lignes
    expect(cloture[5]).toBe("110826"); // date de livraison
    expect(cloture[6]).toBe("411DEMO"); // compte tiers
    expect(cloture[7]).toBe(""); // reference article
    expect(cloture[8]).toBe(""); // designation
    expect(cloture[9]).toBe("0,000000");
    expect(cloture[10]).toBe("0,0000");
    expect(cloture[13]).toBe("0,0000");

    // Une cloture par document, jamais deux.
    const clotures = rows.filter((row) => row[7] === "" && row[8] === "");
    expect(clotures).toHaveLength(2);
    expect(rows[rows.length - 1]![6]).toBe("411AUTRE");
  });

  it("encode en Windows-1252, comme le fichier accepte par Sage", async () => {
    const result = await convert(buffer(), "fne-natif.json", {
      customers: CLIENTS,
      parametres: { depot: "DEPÔT PRINCIPAL" },
    });
    const raw = Buffer.from(result.file.base64, "base64");
    // 0xD4 = O accent circonflexe en Windows-1252 ; en UTF-8 il occuperait deux octets.
    expect(raw.includes(Buffer.from([0xd4]))).toBe(true);
    expect(iconv.decode(raw, "windows-1252")).toContain("DEPÔT PRINCIPAL");
  });

  it("marque l'avoir avec le type de document Sage", async () => {
    const result = await convertir({ normalizeOptions: { numeroPiece: "sequence" } });
    const avoir = result.file.content.split("\r\n").find((row) => row.includes("A2600000004"))!;
    expect(avoir.split("\t")[3]).toBe("5");
  });

  it("laisse la zone numero vide par defaut, comme l'exemplaire reel", async () => {
    const result = await convertir();

    expect(result.file.content.split("\r\n")[0]!.split("\t")[4]).toBe("");
    // La piece vide n'est plus une anomalie, et l'unicite porte sur la reference FNE.
    expect(result.issues.some((issue) => issue.code === "PIECE_MANQUANTE")).toBe(false);
    expect(result.issues.some((issue) => issue.code === "PIECE_DUPLIQUEE")).toBe(false);
  });

  it("ne reclame rien sur la TVA : le format la transporte", async () => {
    const result = await convertir();

    // Le taux etant ecrit ligne a ligne, le regime de la fiche article ne
    // decide plus de rien : ni rappel, ni blocage sur un article a deux taux.
    expect(result.issues.some((issue) => issue.code === "TAXE_ABSENTE_DU_FORMAT")).toBe(false);
    expect(result.issues.some((issue) => issue.code === "ARTICLE_MULTI_TAUX")).toBe(false);
  });

  it("garde une definition purement declarative", () => {
    expect(JSON.parse(JSON.stringify(SAGE100_EXPORT_VERIFIE))).toEqual(SAGE100_EXPORT_VERIFIE);
    expect(SAGE100_EXPORT_VERIFIE.ligne).toHaveLength(14);
    expect(SAGE100_EXPORT_VERIFIE.entete).toHaveLength(0);
    expect(SAGE100_EXPORT_VERIFIE.pied).toHaveLength(14);
  });
});

describe("profil FORMAT IMPORT_EXPORT (temoin)", () => {
  it("porte une zone de plus, en tete", async () => {
    const result = await convertir(ANCIEN);
    const rows = result.file.content.split("\r\n").filter(Boolean).map((row) => row.split("\t"));

    // C'est ce decalage d'un cran que Sage signale : il lit le depot la ou il
    // attend le type de document.
    expect(rows.every((row) => row.length === 15)).toBe(true);
    expect(rows[0]![0]).toBe("0");
    expect(rows[0]![3]).toBe("DEPOT PRINCIPAL");
  });

  it("rappelle que la TVA vient de la fiche article, sans bloquer", async () => {
    const result = await convertir(ANCIEN);
    const issue = result.issues.find((entry) => entry.code === "TAXE_ABSENTE_DU_FORMAT")!;

    // Un article a un regime fixe : melanger 18 % et exonere entre articles est normal.
    expect(issue.severity).toBe("avertissement");
    expect(issue.message).toContain("18 / 0");
    expect(issue.message).toContain("la TVA importee sera fausse");
  });

  it("ne reclame aucune action quand tout est au taux normal", async () => {
    // Meme jeu d'essai, ampute de sa ligne exoneree.
    const source = JSON.parse(readFileSync(FIXTURE, "utf8"));
    source[0].items = [source[0].items[0]];
    const result = await convert(Buffer.from(JSON.stringify(source)), "fne-natif.json", {
      customers: CLIENTS,
      ...ANCIEN,
    });
    const issue = result.issues.find((entry) => entry.code === "TAXE_ABSENTE_DU_FORMAT")!;

    expect(issue.message).toContain("Rien d'autre a faire");
    expect(issue.message).not.toContain("fausse");
  });

  it("bloque un article vu a deux taux differents", async () => {
    const source = JSON.parse(readFileSync(FIXTURE, "utf8"));
    // Le meme article certifie a 18 % sur une facture et exonere sur l'autre.
    source[1].items[0].taxes[0] = { amount: 0, shortName: "TVAD" };
    const result = await convert(Buffer.from(JSON.stringify(source)), "fne-natif.json", {
      customers: CLIENTS,
      ...ANCIEN,
    });

    const issue = result.issues.find((entry) => entry.code === "ARTICLE_MULTI_TAUX")!;
    expect(issue.severity).toBe("erreur");
    expect(issue.message).toContain("ART-001");
    expect(issue.message).toContain("18 / 0");
  });

  it("garde une definition purement declarative", () => {
    expect(JSON.parse(JSON.stringify(SAGE100_IMPORT_EXPORT))).toEqual(SAGE100_IMPORT_EXPORT);
    expect(SAGE100_IMPORT_EXPORT.ligne).toHaveLength(15);
    expect(SAGE100_IMPORT_EXPORT.pied).toHaveLength(15);
  });
});

describe("resume des taux", () => {
  it("resume les taux FNE article par article", async () => {
    const result = await convertir();
    const frites = result.articles.find((article) => article.reference === "ART-001")!;
    const exonere = result.articles.find((article) => article.reference === "ART-002")!;

    expect(frites.taux).toEqual([18]);
    expect(frites.codesTaxe).toEqual(["TVA"]);
    expect(frites.lignes).toBe(2); // la facture et l'avoir
    expect(exonere.taux).toEqual([0]);
    expect(exonere.codesTaxe).toEqual(["TVAC"]);
  });
});

describe("codes de type de document", () => {
  it("laisse le dossier imposer ses propres codes", async () => {
    const result = await convert(buffer(), "fne-natif.json", {
      customers: CLIENTS,
      // Un dossier qui emet ses avoirs de vente sous un autre type.
      parametres: { depot: "D", souche: "1", typeFacture: "6", typeAvoir: "4" },
    });
    const zones = result.file.content.split("\r\n").filter(Boolean).map((row) => row.split("\t"));

    expect(zones[0]![3]).toBe("6");
    expect(zones.find((row) => row[6] === "411AUTRE")![3]).toBe("4");
  });

  it("regle la date de livraison sans toucher a la date du document", async () => {
    const zones = async (options: Record<string, unknown>) => {
      const result = await convertir(options);
      return result.file.content.split("\r\n").filter(Boolean).map((row) => row.split("\t"));
    };

    // Par defaut, la date de livraison reprend celle de la piece.
    const reprise = await zones({});
    expect(reprise[0]![1]).toBe(reprise[0]![5]);

    // Sage peut refuser la date de livraison la ou il accepte celle de la
    // piece : on doit pouvoir la vider sans rien changer d'autre.
    const vide = await zones({ parametres: { depot: "DEPOT PRINCIPAL", dateLivraison: "vide" } });
    expect(vide[0]![5]).toBe("");
    expect(vide[0]![1]).toBe(reprise[0]![1]);
    // Y compris sur la ligne de cloture, qui ne porte que la date de livraison.
    expect(vide.at(-1)![5]).toBe("");

    // Ou la fixer pour tout le fichier.
    const fixe = await zones({
      parametres: { depot: "DEPOT PRINCIPAL", dateLivraison: "31/12/2026" },
    });
    expect(fixe.every((row) => row[5] === "311226")).toBe(true);
  });

  it("ecrit les dates au format demande", async () => {
    const result = await convertir({ formatDate: "DDMMYYYY" });
    const premiere = result.file.content.split("\r\n")[0]!.split("\t");

    // Le format du profil est jjmmaa : un dossier qui attend jjmmaaaa refuse
    // la piece en ne nommant que la zone de date.
    expect(premiere[1]).toMatch(/^\d{8}$/);
    expect(premiere[5]).toBe(premiere[1]);
  });
});
