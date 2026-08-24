import { formatDate, parseDate } from "@/lib/core/date";
import { Invoice, InvoiceLine } from "@/lib/core/model";
import { formatNumber } from "@/lib/core/text";
import { codeReglementSage, libellePaiement, PaymentMapping } from "@/lib/fne/paiement";
import { findTaxCode } from "@/lib/fne/taxes";
import { SageImportProfile } from "./profile";

export interface TokenContext {
  invoice: Invoice;
  line: InvoiceLine | null;
  profile: SageImportProfile;
  /** Correspondance mode de paiement FNE -> code reglement Sage. */
  reglements?: PaymentMapping;
  /**
   * Valeurs propres au dossier Sage (depot, souche, code affaire...),
   * referencees par les jetons `parametre.<nom>`.
   */
  parametres?: Record<string, string>;
  /**
   * Nombre de decimales impose par la colonne du profil. Prioritaire sur la
   * valeur par defaut du jeton : une zone "quantite a 4 decimales" doit
   * l'emporter sur les 3 decimales usuelles.
   */
  decimalsOverride?: number;
}

type TokenResolver = (ctx: TokenContext) => string;

function num(value: number, ctx: TokenContext, decimals?: number): string {
  return formatNumber(
    value,
    ctx.decimalsOverride ?? decimals ?? ctx.profile.decimals,
    ctx.profile.decimalSeparator,
  );
}

/**
 * Jetons utilisables dans un profil d'import.
 * Ajouter une zone au fichier Sage = referencer un jeton, pas ecrire du code.
 */
export const TOKENS: Record<string, TokenResolver> = {
  // Le code du type de document depend du dossier Sage : tous n'emettent pas
  // les avoirs de vente sous le meme type. Un parametre le remplace donc,
  // sans quoi un dossier qui attend un autre code refuse tout le fichier.
  "document.type": (ctx) => {
    const parDefaut =
      ctx.invoice.kind === "AVOIR"
        ? ctx.profile.documentTypes.avoir
        : ctx.profile.documentTypes.facture;
    const parametre = ctx.invoice.kind === "AVOIR" ? "typeAvoir" : "typeFacture";
    return ctx.parametres?.[parametre] || parDefaut;
  },
  "document.numero": (ctx) => ctx.invoice.numero,
  "document.date": (ctx) => (ctx.invoice.date ? formatDate(ctx.invoice.date, ctx.profile.dateFormat) : ""),
  /**
   * Date de livraison du document.
   *
   * Sage la controle a l'import, et un dossier peut la refuser la ou il
   * accepte la date du document : elle se regle donc a part - reprise de la
   * date du document (defaut), laissee vide, ou fixee a une date donnee.
   */
  "document.dateLivraison": (ctx) => {
    const consigne = (ctx.parametres?.dateLivraison ?? "").trim();
    if (consigne === "vide") return "";
    if (consigne !== "" && consigne !== "document") {
      const iso = parseDate(consigne);
      return iso ? formatDate(iso, ctx.profile.dateFormat) : "";
    }
    return ctx.invoice.date ? formatDate(ctx.invoice.date, ctx.profile.dateFormat) : "";
  },
  "document.reference": (ctx) => ctx.invoice.reference,
  "document.devise": (ctx) => ctx.invoice.devise,
  "document.modeReglement": (ctx) => ctx.invoice.modeReglement,
  "document.modeReglementLibelle": (ctx) => libellePaiement(ctx.invoice.modeReglement),
  "document.codeReglement": (ctx) => codeReglementSage(ctx.invoice.modeReglement, ctx.reglements ?? {}),
  "document.numeroParent": (ctx) => ctx.invoice.numeroParent,
  "document.template": (ctx) => ctx.invoice.template,
  "document.vendeur": (ctx) => ctx.invoice.vendeur,
  "document.pointDeVente": (ctx) => ctx.invoice.pointDeVente,
  "document.etablissement": (ctx) => ctx.invoice.etablissement,
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
  "totaux.timbre": (ctx) => num(ctx.invoice.totaux.timbre, ctx),
  "totaux.netAPayer": (ctx) => num(ctx.invoice.totaux.netAPayer, ctx),

  "ligne.numero": (ctx) => (ctx.line ? String(ctx.line.numero) : ""),
  "ligne.reference": (ctx) => ctx.line?.referenceArticle ?? "",
  "ligne.designation": (ctx) => ctx.line?.designation ?? "",
  "ligne.quantite": (ctx) => (ctx.line ? num(ctx.line.quantite, ctx, 3) : ""),
  "ligne.unite": (ctx) => ctx.line?.unite ?? "",
  "ligne.prixUnitaire": (ctx) => (ctx.line ? num(ctx.line.prixUnitaireHT, ctx) : ""),
  "ligne.remise": (ctx) => (ctx.line ? num(ctx.line.remisePourcent, ctx, 2) : ""),
  "ligne.tauxTva": (ctx) => (ctx.line ? num(ctx.line.tauxTva, ctx, 2) : ""),
  "ligne.codeTaxe": (ctx) => ctx.line?.codeTaxeFne ?? "",
  /**
   * Code taxe tel que le dossier Sage l'ecrit.
   *
   * Dans l'exemplaire verifie, le meme libelle "TVA" porte le taux normal
   * comme le taux reduit, et la zone reste vide sur une ligne exoneree : la
   * nomenclature FNE (TVA, TVAB, TVAC, TVAD) n'est pas celle de Sage, et le
   * libelle se regle par parametre.
   *
   * Un code hors de cette nomenclature - l'AIRSI a 1,5 % du meme exemplaire -
   * designe une autre taxe que la TVA : il est alors repris tel quel.
   */
  "ligne.codeTaxeSage": (ctx) => {
    if (!ctx.line) return "";
    const code = ctx.line.codeTaxeFne;
    if (code && !findTaxCode(code)) return code;
    if (ctx.line.tauxTva === 0) return "";
    return ctx.parametres?.codeTaxe || "TVA";
  },
  "ligne.montantHT": (ctx) => (ctx.line ? num(ctx.line.montantHT, ctx) : ""),
  "ligne.montantTva": (ctx) => (ctx.line ? num(ctx.line.montantTva, ctx) : ""),
  "ligne.montantTTC": (ctx) => (ctx.line ? num(ctx.line.montantTTC, ctx) : ""),
};

export const TOKEN_NAMES = Object.keys(TOKENS).sort();

/** Jeton `parametre.<nom>` : valeur saisie par l'utilisateur pour son dossier Sage. */
function resolveParametre(token: string, ctx: TokenContext): string {
  const nom = token.slice("parametre.".length);
  return ctx.parametres?.[nom] ?? "";
}

export function resolveToken(token: string, ctx: TokenContext): string {
  if (token.startsWith("parametre.")) return resolveParametre(token, ctx);
  const resolver = TOKENS[token];
  if (!resolver) throw new Error(`Jeton inconnu dans le profil d'import : "${token}"`);
  return resolver(ctx);
}

/** Jetons `parametre.<nom>` attendus par les profils livres. */
export const PARAMETRES_CONNUS: Array<{ nom: string; libelle: string; defaut: string }> = [
  { nom: "depot", libelle: "Depot", defaut: "" },
  { nom: "souche", libelle: "Souche", defaut: "1" },
  // Vides : le profil fournit alors ses propres codes (6 facture, 5 avoir).
  { nom: "typeFacture", libelle: "Type de document facture", defaut: "" },
  { nom: "typeAvoir", libelle: "Type de document avoir", defaut: "" },
  // "document" reprend la date de la piece, "vide" laisse la zone vide, une
  // date la fixe pour tout le fichier.
  { nom: "dateLivraison", libelle: "Date de livraison", defaut: "document" },
  { nom: "codeTaxe", libelle: "Code taxe Sage", defaut: "TVA" },
];
