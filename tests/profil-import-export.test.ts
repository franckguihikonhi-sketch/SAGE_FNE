import { readFileSync } from "node:fs";
import iconv from "iconv-lite";
import { describe, expect, it } from "vitest";
import { convertFichier as convert } from "@/lib/node/convert";
import { SAGE100_IMPORT_EXPORT } from "@/lib/sage/profile";

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

describe("profil FORMAT IMPORT_EXPORT", () => {
  it("est le profil par defaut", async () => {
    const result = await convertir();
    expect(result.profile.id).toBe("sage100-import-export");
  });

  it("produit un fichier a plat de 15 zones tabulees", async () => {
    const result = await convertir();
    const decoded = iconv.decode(Buffer.from(result.file.base64, "base64"), "windows-1252");
    const rows = decoded.split("\r\n").filter(Boolean);

    // Format a plat : une ligne par article, pas d'enregistrement d'entete.
    expect(rows).toHaveLength(3);
    expect(rows.every((row) => row.split("\t").length === 15)).toBe(true);
    expect(decoded.endsWith("\r\n")).toBe(true);
  });

  it("ecrit les zones dans l'ordre du format du dossier client", async () => {
    const result = await convertir();
    const decoded = iconv.decode(Buffer.from(result.file.base64, "base64"), "windows-1252");
    const cells = decoded.split("\r\n")[0]!.split("\t");

    expect(cells[0]).toBe("0"); // domaine vente
    expect(cells[1]).toBe("26000000123"); // numero de piece
    expect(cells[2]).toBe("110826"); // date au format jjmmaa
    expect(cells[3]).toBe("DEPOT PRINCIPAL");
    expect(cells[4]).toBe("6"); // facture
    expect(cells[5]).toBe("1"); // souche
    expect(cells[6]).toBe("110826"); // date de livraison
    expect(cells[7]).toBe("411DEMO");
    expect(cells[8]).toBe("ART-001");
    expect(cells[9]).toBe("FRITES 7MM-PK (4*2.5kg)");
    expect(cells[10]).toBe("1077,276300"); // prix unitaire, 6 decimales
    expect(cells[11]).toBe("20,0000"); // quantite, 4 decimales
    expect(cells[12]).toBe("SAC");
    expect(cells[13]).toBe("");
    expect(cells[14]).toBe("0,0000"); // remise, 4 decimales
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
    const result = await convertir();
    const decoded = iconv.decode(Buffer.from(result.file.base64, "base64"), "windows-1252");
    const avoir = decoded.split("\r\n").find((row) => row.includes("A2600000004"))!;
    expect(avoir.split("\t")[4]).toBe("5");
  });

  it("laisse la zone numero vide sous numerotation Sage", async () => {
    const result = await convertir({ normalizeOptions: { numeroPiece: "vide" } });
    const decoded = iconv.decode(Buffer.from(result.file.base64, "base64"), "windows-1252");

    expect(decoded.split("\r\n")[0]!.split("\t")[1]).toBe("");
    // La piece vide n'est plus une anomalie, et l'unicite porte sur la reference FNE.
    expect(result.issues.some((issue) => issue.code === "PIECE_MANQUANTE")).toBe(false);
    expect(result.issues.some((issue) => issue.code === "PIECE_DUPLIQUEE")).toBe(false);
  });

  it("rappelle que la TVA vient de la fiche article, sans bloquer", async () => {
    const result = await convertir();
    const issue = result.issues.find((entry) => entry.code === "TAXE_ABSENTE_DU_FORMAT")!;

    // Un article a un regime fixe : melanger 18 % et exonere entre articles est normal.
    expect(issue.severity).toBe("avertissement");
    expect(issue.message).toContain("18 / 0");
  });

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

  it("bloque un article vu a deux taux differents", async () => {
    const source = JSON.parse(readFileSync(FIXTURE, "utf8"));
    // Le meme article certifie a 18 % sur une facture et exonere sur l'autre.
    source[1].items[0].taxes[0] = { amount: 0, shortName: "TVAD" };
    const result = await convert(Buffer.from(JSON.stringify(source)), "fne-natif.json", {
      customers: CLIENTS,
    });

    const issue = result.issues.find((entry) => entry.code === "ARTICLE_MULTI_TAUX")!;
    expect(issue.severity).toBe("erreur");
    expect(issue.message).toContain("ART-001");
    expect(issue.message).toContain("18 / 0");
  });

  it("ne signale rien sur un profil qui porte la taxe", async () => {
    const result = await convertir({ profileId: "sage100-documents-ventes" });
    expect(result.issues.some((issue) => issue.code === "TAXE_ABSENTE_DU_FORMAT")).toBe(false);
  });

  it("garde une definition purement declarative", () => {
    expect(JSON.parse(JSON.stringify(SAGE100_IMPORT_EXPORT))).toEqual(SAGE100_IMPORT_EXPORT);
    expect(SAGE100_IMPORT_EXPORT.ligne).toHaveLength(15);
    expect(SAGE100_IMPORT_EXPORT.entete).toHaveLength(0);
  });
});
