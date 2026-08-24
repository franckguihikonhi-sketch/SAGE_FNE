import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { emptyCustomer, emptyInvoice, Invoice } from "@/lib/core/model";
import { buildSageFile } from "@/lib/sage/export";
import { SAGE100_EXPORT_VERIFIE } from "@/lib/sage/profile";

/**
 * Aller-retour sur un exemplaire du format que le dossier importe.
 *
 * Le fichier est relu, ses documents reconstruits, puis reecrits par le
 * connecteur : toute difference serait un ecart de notre ecriture, puisque les
 * donnees sont les memes. C'est le seul controle qui prouve que le fichier
 * produit est bien celui que Sage accepte.
 *
 * Le contenu est anonymise, mais la structure est celle relevee sur
 * l'exemplaire reel : prelevement AIRSI a 1,5 %, ligne exoneree, taux reduit,
 * taux normal, document a deux lignes, type 8 a numerotation distincte, et une
 * piece dont la date de livraison differe de la date du document.
 */
const FIXTURE = new URL("./fixtures/sage-export-verifie.txt", import.meta.url);

const nombre = (valeur: string) => Number(valeur.replace(",", ".") || "0");
const jjmmaa = (valeur: string) =>
  valeur.length === 6 ? `20${valeur.slice(4, 6)}-${valeur.slice(2, 4)}-${valeur.slice(0, 2)}` : "";

interface Reconstruit {
  invoice: Invoice;
  type: string;
  depot: string;
  dateLivraison: string;
}

function relire(contenu: string): Reconstruit[] {
  const documents = new Map<string, Reconstruit>();
  const ordre: string[] = [];

  for (const ligne of contenu.split("\r\n").filter((l) => l !== "")) {
    const z = ligne.split("\t");
    const cle = `${z[3]}|${z[4]}`;
    let document = documents.get(cle);
    if (!document) {
      const invoice = emptyInvoice();
      invoice.numero = z[4]!;
      invoice.date = jjmmaa(z[1]!);
      invoice.client = { ...emptyCustomer(), code: z[6]! };
      document = { invoice, type: z[3]!, depot: z[2]!, dateLivraison: z[5]! };
      documents.set(cle, document);
      ordre.push(cle);
    }
    document.invoice.lignes.push({
      numero: document.invoice.lignes.length + 1,
      referenceArticle: z[7]!,
      designation: z[8]!,
      quantite: nombre(z[10]!),
      prixUnitaireHT: nombre(z[9]!),
      remisePourcent: 0,
      tauxTva: nombre(z[13]!),
      codeTaxeFne: z[12]!,
      montantHT: 0,
      montantTva: 0,
      montantTTC: 0,
      unite: z[11]!,
      sourceRow: 0,
    });
  }

  return ordre.map((cle) => documents.get(cle)!);
}

describe("format que le dossier importe", () => {
  it("reecrit l'exemplaire octet pour octet", () => {
    const attendu = readFileSync(FIXTURE);
    const documents = relire(attendu.toString("latin1"));

    // Un document par appel : le depot, le type et la date de livraison sont
    // ceux lus dans le fichier, comme le seraient les reglages du poste.
    const morceaux = documents.map(({ invoice, type, depot, dateLivraison }) =>
      Buffer.from(
        buildSageFile([invoice], SAGE100_EXPORT_VERIFIE, "controle", {
          parametres: {
            depot,
            typeFacture: type,
            typeAvoir: type,
            dateLivraison: `${dateLivraison.slice(0, 2)}/${dateLivraison.slice(2, 4)}/20${dateLivraison.slice(4, 6)}`,
            codeTaxe: "TVA",
          },
        }).buffer,
      ),
    );

    expect(Buffer.concat(morceaux).equals(attendu)).toBe(true);
  });

  it("n'ecrit aucune ligne de cloture", () => {
    // Sur les soixante enregistrements de l'exemplaire reel, cinquante-sept
    // documents n'en portent aucune : ce n'est pas une regle du format.
    expect(SAGE100_EXPORT_VERIFIE.pied).toBeUndefined();
  });

  it("reprend un code taxe hors nomenclature FNE tel quel", () => {
    const documents = relire(readFileSync(FIXTURE).toString("latin1"));
    const airsi = documents[0]!;
    const ligne = buildSageFile([airsi.invoice], SAGE100_EXPORT_VERIFIE, "controle", {
      parametres: { depot: airsi.depot, codeTaxe: "TVA" },
    })
      .preview.split("\r\n")[0]!
      .split("\t");

    // L'AIRSI n'est pas une TVA : le libelle du parametre ne doit pas l'ecraser.
    expect(ligne[12]).toBe("AIRSI");
    expect(ligne[13]).toBe("1,5000");
  });
});
