#!/usr/bin/env node
/**
 * Conversion en ligne de commande, utile pour tester un export FNE reel
 * sans passer par l'interface web.
 *
 *   npm run convert -- export-fne.csv --profil=sage100-documents-ventes --clients=clients.csv
 */
import { readFileSync, writeFileSync } from "node:fs";
import { basename, resolve } from "node:path";
import { convert } from "@/lib/pipeline";
import { parseCustomerMappingCsv } from "@/lib/sage/customers";
import { parsePaymentMappingText } from "@/lib/fne/paiement";
import { PROFILES } from "@/lib/sage/profile";

async function main() {
  const args = process.argv.slice(2);
  const input = args.find((arg) => !arg.startsWith("--"));
  if (!input) {
    console.error("Usage : npm run convert -- <export-fne.csv|xlsx|json> [options]\n");
    console.error("Options :");
    console.error("  --profil=<id>      Format d'import Sage a utiliser");
    console.error("  --clients=<csv>    Table de correspondance NCC/nom -> compte tiers Sage");
    console.error("  --defaut=<compte>  Compte tiers par defaut");
    console.error("  --sortie=<fichier> Chemin du fichier genere");
    console.error("  --feuille=<nom>    Feuille Excel a lire");
    console.error("  --detail=<n>       Nombre d'anomalies detaillees (defaut 10)");
    console.error("  --depot=<nom>      Depot Sage porte par les documents");
    console.error("  --souche=<n>       Souche Sage (defaut 1)");
    console.error("  --reglements=<f>   Fichier de correspondance des modes de reglement");
    console.error("  --numero=<mode>    sequence (defaut), reference ou vide\n");
    console.error("Profils disponibles :");
    for (const profile of PROFILES) console.error(`  ${profile.id.padEnd(28)} ${profile.label}`);
    process.exit(1);
  }

  const option = (name: string) =>
    args.find((arg) => arg.startsWith(`--${name}=`))?.split("=").slice(1).join("=");

  const buffer = readFileSync(resolve(input));
  const clientsPath = option("clients");
  const reglementsPath = option("reglements");
  const numero = option("numero");
  const parametres: Record<string, string> = {};
  const depot = option("depot");
  const souche = option("souche");
  if (depot !== undefined) parametres.depot = depot;
  if (souche !== undefined) parametres.souche = souche;

  const result = await convert(buffer, basename(input), {
    profileId: option("profil"),
    sheet: option("feuille"),
    customers: clientsPath ? parseCustomerMappingCsv(readFileSync(resolve(clientsPath), "utf8")) : [],
    customerOptions: { compteParDefaut: option("defaut"), utiliserCodeSource: true },
    reglements: reglementsPath
      ? parsePaymentMappingText(readFileSync(resolve(reglementsPath), "utf8"))
      : {},
    parametres,
    normalizeOptions:
      numero === "reference" || numero === "vide" || numero === "sequence"
        ? { numeroPiece: numero }
        : {},
  });

  const output = option("sortie") ?? result.file.filename;
  writeFileSync(output, Buffer.from(result.file.base64, "base64"));

  const nature = result.source.kind === "fne-json" ? "export JSON natif FNE" : "tableau";
  console.log(
    `Source        : ${result.source.format.toUpperCase()} (${nature}) - ${result.source.rowCount} enregistrements lus`,
  );
  if (result.source.synthese) {
    console.log("                detail des articles absent : lignes de synthese generees");
  }
  console.log(`Profil Sage   : ${result.profile.label}`);
  console.log(
    `Documents     : ${result.summary.factures} facture(s), ${result.summary.avoirs} avoir(s), ` +
      `${result.summary.lignes} ligne(s)`,
  );
  const fmt = (value: number) => value.toLocaleString("fr-FR", { maximumFractionDigits: 2 });
  console.log(
    `Totaux        : HT ${fmt(result.summary.totalHT)} | TVA ${fmt(result.summary.totalTva)} | ` +
      `TTC ${fmt(result.summary.totalTTC)}`,
  );
  console.log(`Fichier genere: ${output} (${result.file.lineCount} enregistrements)`);

  if (result.articles.length > 0) {
    console.log("\nTVA par article (regime a verifier sur la fiche article Sage) :");
    for (const article of result.articles.slice(0, 15)) {
      const taux = article.taux.map((valeur) => `${valeur} %`).join(" / ");
      const codes = article.codesTaxe.join(", ");
      console.log(
        `  ${(article.reference || "-").padEnd(12)} ${taux.padEnd(14)} ${codes.padEnd(6)} ` +
          `${article.lignes} ligne(s)  ${article.designation}`,
      );
    }
    if (result.articles.length > 15) console.log(`  ... et ${result.articles.length - 15} autres articles`);
  }

  if (result.unmappedColumns.length > 0) {
    console.log(`\nColonnes non reconnues : ${result.unmappedColumns.join(", ")}`);
  }
  if (result.ignoredColumns.length > 0) {
    console.log(`Colonnes ignorees (sans usage Sage) : ${result.ignoredColumns.join(", ")}`);
  }
  if (result.missingFields.length > 0) {
    console.log(`Champs obligatoires manquants : ${result.missingFields.join(", ")}`);
  }

  const erreurs = result.issues.filter((issue) => issue.severity === "erreur");
  const avertissements = result.issues.filter((issue) => issue.severity === "avertissement");

  if (result.issues.length > 0) {
    const parCode = new Map<string, number>();
    for (const issue of result.issues) parCode.set(issue.code, (parCode.get(issue.code) ?? 0) + 1);
    console.log(`\nControles : ${erreurs.length} erreur(s), ${avertissements.length} avertissement(s)`);
    for (const [code, count] of [...parCode].sort((a, b) => b[1] - a[1])) {
      console.log(`  ${String(count).padStart(4)} x ${code}`);
    }
  }

  const detail = Number(option("detail") ?? 10);
  for (const [titre, liste] of [["Erreurs", erreurs], ["Avertissements", avertissements]] as const) {
    if (liste.length === 0) continue;
    console.log(`\n${titre} :`);
    for (const issue of liste.slice(0, detail)) console.log(`  [${issue.facture ?? "-"}] ${issue.message}`);
    if (liste.length > detail) console.log(`  ... et ${liste.length - detail} autres (--detail=N pour en voir plus)`);
  }
  if (erreurs.length > 0) process.exitCode = 2;
}

main().catch((error: unknown) => {
  console.error(error instanceof Error ? error.message : error);
  process.exit(1);
});
