import { Invoice } from "@/lib/core/model";
import { normalizeKey } from "@/lib/core/text";

/**
 * Table de correspondance entre les clients de l'export FNE et les comptes
 * tiers Sage. L'export FNE identifie le client par son NCC ou sa raison
 * sociale ; Sage exige un numero de compte tiers existant.
 */
export interface CustomerMappingEntry {
  /** NCC du client tel qu'il figure dans l'export FNE. */
  ncc?: string;
  /** Raison sociale, utilisee quand le NCC est absent. */
  nom?: string;
  /** Numero de compte tiers Sage (CT_Num). */
  codeSage: string;
}

export interface CustomerMappingOptions {
  /** Compte tiers utilise quand aucune correspondance n'est trouvee (ex. client divers). */
  compteParDefaut?: string;
  /**
   * Si vrai, le code client present dans l'export FNE est utilise tel quel
   * quand aucune correspondance n'existe.
   */
  utiliserCodeSource?: boolean;
}

export interface CustomerMappingResult {
  invoices: Invoice[];
  /** Clients sans correspondance : a creer dans Sage ou a ajouter a la table. */
  inconnus: Array<{ nom: string; ncc: string; factures: string[] }>;
}

export function applyCustomerMapping(
  invoices: Invoice[],
  entries: CustomerMappingEntry[],
  options: CustomerMappingOptions = {},
): CustomerMappingResult {
  const byNcc = new Map<string, string>();
  const byNom = new Map<string, string>();
  for (const entry of entries) {
    if (entry.ncc) byNcc.set(normalizeKey(entry.ncc), entry.codeSage);
    if (entry.nom) byNom.set(normalizeKey(entry.nom), entry.codeSage);
  }

  const inconnus = new Map<string, { nom: string; ncc: string; factures: string[] }>();

  const mapped = invoices.map((invoice) => {
    const ncc = normalizeKey(invoice.client.ncc);
    const nom = normalizeKey(invoice.client.nom);
    const code =
      (ncc ? byNcc.get(ncc) : undefined) ??
      (nom ? byNom.get(nom) : undefined) ??
      (options.utiliserCodeSource && invoice.client.code ? invoice.client.code : undefined) ??
      options.compteParDefaut ??
      "";

    if (!code || (!byNcc.has(ncc) && !byNom.has(nom) && !options.compteParDefaut && !invoice.client.code)) {
      const key = ncc || nom || invoice.numero;
      const existing = inconnus.get(key);
      if (existing) existing.factures.push(invoice.numero);
      else inconnus.set(key, { nom: invoice.client.nom, ncc: invoice.client.ncc, factures: [invoice.numero] });
    }

    return { ...invoice, client: { ...invoice.client, code } };
  });

  return { invoices: mapped, inconnus: [...inconnus.values()] };
}

/** Lit une table de correspondance CSV a deux ou trois colonnes : ncc;nom;codeSage. */
export function parseCustomerMappingCsv(text: string): CustomerMappingEntry[] {
  const entries: CustomerMappingEntry[] = [];
  const lines = text.split(/\r?\n/).filter((line) => line.trim() !== "");
  for (const [index, line] of lines.entries()) {
    const cells = line.split(/[;,\t]/).map((cell) => cell.trim().replace(/^"|"$/g, ""));
    if (index === 0 && /ncc|nom|code/i.test(line) && !/^\d/.test(cells[0] ?? "")) continue; // entete
    if (cells.length < 2) continue;
    if (cells.length === 2) {
      const [identifiant = "", codeSage = ""] = cells;
      if (!codeSage) continue;
      entries.push(/^[0-9]/.test(identifiant) ? { ncc: identifiant, codeSage } : { nom: identifiant, codeSage });
      continue;
    }
    const [ncc = "", nom = "", codeSage = ""] = cells;
    if (!codeSage) continue;
    entries.push({ ncc: ncc || undefined, nom: nom || undefined, codeSage });
  }
  return entries;
}
