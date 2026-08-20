import { formatDate } from "@/lib/core/date";
import { Invoice, InvoiceLine } from "@/lib/core/model";
import { formatNumber } from "@/lib/core/text";
import { SageImportProfile } from "./profile";

export interface TokenContext {
  invoice: Invoice;
  line: InvoiceLine | null;
  profile: SageImportProfile;
}

type TokenResolver = (ctx: TokenContext) => string;

function num(value: number, ctx: TokenContext, decimals?: number): string {
  return formatNumber(value, decimals ?? ctx.profile.decimals, ctx.profile.decimalSeparator);
}

/**
 * Jetons utilisables dans un profil d'import.
 * Ajouter une zone au fichier Sage = referencer un jeton, pas ecrire du code.
 */
export const TOKENS: Record<string, TokenResolver> = {
  "document.type": (ctx) =>
    ctx.invoice.kind === "AVOIR" ? ctx.profile.documentTypes.avoir : ctx.profile.documentTypes.facture,
  "document.numero": (ctx) => ctx.invoice.numero,
  "document.date": (ctx) => (ctx.invoice.date ? formatDate(ctx.invoice.date, ctx.profile.dateFormat) : ""),
  "document.reference": (ctx) => ctx.invoice.reference,
  "document.devise": (ctx) => ctx.invoice.devise,
  "document.modeReglement": (ctx) => ctx.invoice.modeReglement,
  "document.commentaire": (ctx) => ctx.invoice.commentaire,
  "document.numeroFne": (ctx) => ctx.invoice.numeroFne,
  "document.codeVerification": (ctx) => ctx.invoice.codeVerification,
  "document.nbLignes": (ctx) => String(ctx.invoice.lignes.length),

  "client.code": (ctx) => ctx.invoice.client.code,
  "client.nom": (ctx) => ctx.invoice.client.nom,
  "client.ncc": (ctx) => ctx.invoice.client.ncc,
  "client.adresse": (ctx) => ctx.invoice.client.adresse,
  "client.telephone": (ctx) => ctx.invoice.client.telephone,
  "client.email": (ctx) => ctx.invoice.client.email,

  "totaux.ht": (ctx) => num(ctx.invoice.totaux.totalHT, ctx),
  "totaux.tva": (ctx) => num(ctx.invoice.totaux.totalTva, ctx),
  "totaux.ttc": (ctx) => num(ctx.invoice.totaux.totalTTC, ctx),
  "totaux.remise": (ctx) => num(ctx.invoice.totaux.totalRemise, ctx),
  "totaux.autresTaxes": (ctx) => num(ctx.invoice.totaux.autresTaxes, ctx),

  "ligne.numero": (ctx) => (ctx.line ? String(ctx.line.numero) : ""),
  "ligne.reference": (ctx) => ctx.line?.referenceArticle ?? "",
  "ligne.designation": (ctx) => ctx.line?.designation ?? "",
  "ligne.quantite": (ctx) => (ctx.line ? num(ctx.line.quantite, ctx, 3) : ""),
  "ligne.unite": (ctx) => ctx.line?.unite ?? "",
  "ligne.prixUnitaire": (ctx) => (ctx.line ? num(ctx.line.prixUnitaireHT, ctx) : ""),
  "ligne.remise": (ctx) => (ctx.line ? num(ctx.line.remisePourcent, ctx, 2) : ""),
  "ligne.tauxTva": (ctx) => (ctx.line ? num(ctx.line.tauxTva, ctx, 2) : ""),
  "ligne.codeTaxe": (ctx) => ctx.line?.codeTaxeFne ?? "",
  "ligne.montantHT": (ctx) => (ctx.line ? num(ctx.line.montantHT, ctx) : ""),
  "ligne.montantTva": (ctx) => (ctx.line ? num(ctx.line.montantTva, ctx) : ""),
  "ligne.montantTTC": (ctx) => (ctx.line ? num(ctx.line.montantTTC, ctx) : ""),
};

export const TOKEN_NAMES = Object.keys(TOKENS).sort();

export function resolveToken(token: string, ctx: TokenContext): string {
  const resolver = TOKENS[token];
  if (!resolver) throw new Error(`Jeton inconnu dans le profil d'import : "${token}"`);
  return resolver(ctx);
}
