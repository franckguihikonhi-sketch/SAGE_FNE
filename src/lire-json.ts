import { Facture, Ligne, Nature } from "./modele";
import { arrondir, dateIso } from "./texte";

/**
 * Lecture de l'export JSON de FNE, quand la plateforme le donne.
 *
 * Il porte les prix unitaires a leur precision certifiee, la ou le PDF les
 * arrondit au franc. Quand il est disponible, c'est la meilleure source.
 */

interface TaxeJson {
  amount?: number;
  name?: string;
  shortName?: string;
}

interface ItemJson {
  reference?: string;
  description?: string;
  quantity?: number;
  amount?: number;
  measurementUnit?: string | null;
  taxes?: TaxeJson[];
  customTaxes?: TaxeJson[];
}

interface FactureJson {
  reference?: string;
  subtype?: string;
  date?: string;
  clientNcc?: string | null;
  clientCompanyName?: string | null;
  totalBeforeTaxes?: number;
  totalTaxes?: number;
  totalCustomTaxes?: number;
  items?: ItemJson[];
}

export function estExportJson(texte: string): boolean {
  const debut = texte.trimStart()[0];
  return debut === "[" || debut === "{";
}

export function lireJson(texte: string): { factures: Facture[]; avertissements: string[] } {
  const avertissements: string[] = [];
  let charge: unknown;
  try {
    charge = JSON.parse(texte);
  } catch {
    return { factures: [], avertissements: ["Ce fichier JSON est illisible."] };
  }

  const enregistrements = extraire(charge);
  if (enregistrements.length === 0) {
    return { factures: [], avertissements: ["Aucune facture dans ce fichier JSON."] };
  }

  const factures = enregistrements.map((source, index) =>
    construire(source as FactureJson, index + 1, avertissements),
  );
  return { factures, avertissements };
}

function extraire(charge: unknown): unknown[] {
  if (Array.isArray(charge)) return charge;
  if (charge && typeof charge === "object") {
    const objet = charge as Record<string, unknown>;
    for (const nom of ["data", "items", "invoices", "factures", "results", "content"]) {
      if (Array.isArray(objet[nom])) return objet[nom] as unknown[];
    }
    if (objet.invoice && typeof objet.invoice === "object") return [objet.invoice];
    if (typeof objet.reference === "string") return [objet];
  }
  return [];
}

function construire(source: FactureJson, rang: number, avertissements: string[]): Facture {
  const reference = (source.reference ?? "").trim();
  const nature: Nature = source.subtype === "refund" ? "AVOIR" : "FACTURE";
  // FNE certifie les avoirs en negatif ; Sage les exprime par le type de
  // document et attend des montants positifs.
  const signe = nature === "AVOIR" ? -1 : 1;
  const piece = reference || `facture ${rang} du fichier`;

  const date = dateIso(source.date ?? "") || (source.date ?? "").slice(0, 10);
  if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) {
    avertissements.push(`${piece} : date absente ou illisible.`);
  }

  const items = Array.isArray(source.items) ? source.items : [];
  if (items.length === 0) avertissements.push(`${piece} : aucun article dans l'export.`);

  return {
    reference,
    date: /^\d{4}-\d{2}-\d{2}$/.test(date) ? date : "",
    nature,
    client: {
      nom: (source.clientCompanyName ?? "").trim(),
      ncc: (source.clientNcc ?? "").trim(),
      compte: "",
    },
    lignes: items.map((item) => ligne(item, signe)),
    totalHT: arrondir(signe * (source.totalBeforeTaxes ?? 0), 2),
    totalTva: arrondir(signe * (source.totalTaxes ?? 0), 2),
    totalAutresTaxes: arrondir(signe * (source.totalCustomTaxes ?? 0), 2),
    rang,
  };
}

function ligne(item: ItemJson, signe: number): Ligne {
  const quantite = item.quantity ?? 0;
  // `amount` est le prix unitaire HT, pas le total de ligne (annexe 1 DGI).
  const prixUnitaire = arrondir(signe * (item.amount ?? 0), 6);
  const tva = (item.taxes ?? []).find((taxe) => (taxe.amount ?? 0) > 0);
  const prelevement = (item.customTaxes ?? []).find((taxe) => (taxe.amount ?? 0) > 0);

  const { codeTaxe, taux } = tva
    ? { codeTaxe: "TVA", taux: tva.amount ?? 0 }
    : prelevement
      ? { codeTaxe: (prelevement.shortName ?? "").toUpperCase() || "AIRSI", taux: prelevement.amount ?? 0 }
      : { codeTaxe: "", taux: 0 };

  return {
    reference: (item.reference ?? "").trim(),
    designation: (item.description ?? "").trim(),
    unite: (item.measurementUnit ?? "").trim().toUpperCase(),
    quantite,
    prixUnitaire,
    montantHT: arrondir(quantite * prixUnitaire, 2),
    codeTaxe,
    taux,
  };
}
