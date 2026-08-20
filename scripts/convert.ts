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
    console.error("  --feuille=<nom>    Feuille Excel a lire\n");
    console.error("Profils disponibles :");
    for (const profile of PROFILES) console.error(`  ${profile.id.padEnd(28)} ${profile.label}`);
    process.exit(1);
  }

  const option = (name: string) =>
    args.find((arg) => arg.startsWith(`--${name}=`))?.split("=").slice(1).join("=");

  const buffer = readFileSync(resolve(input));
  const clientsPath = option("clients");
  const result = await convert(buffer, basename(input), {
    profileId: option("profil"),
    sheet: option("feuille"),
    customers: clientsPath ? parseCustomerMappingCsv(readFileSync(resolve(clientsPath), "utf8")) : [],
    customerOptions: { compteParDefaut: option("defaut"), utiliserCodeSource: true },
  });

  const output = option("sortie") ?? result.file.filename;
  writeFileSync(output, Buffer.from(result.file.base64, "base64"));

  console.log(`Source        : ${result.table.format.toUpperCase()} - ${result.table.rowCount} lignes lues`);
  console.log(`Profil Sage   : ${result.profile.label}`);
  console.log(
    `Documents     : ${result.summary.factures} facture(s), ${result.summary.avoirs} avoir(s), ` +
      `${result.summary.lignes} ligne(s)`,
  );
  console.log(
    `Totaux        : HT ${result.summary.totalHT} | TVA ${result.summary.totalTva} | TTC ${result.summary.totalTTC}`,
  );
  console.log(`Fichier genere: ${output} (${result.file.lineCount} enregistrements)`);

  if (result.unmappedColumns.length > 0) {
    console.log(`\nColonnes non reconnues : ${result.unmappedColumns.join(", ")}`);
  }
  if (result.missingFields.length > 0) {
    console.log(`Champs obligatoires manquants : ${result.missingFields.join(", ")}`);
  }

  const erreurs = result.issues.filter((issue) => issue.severity === "erreur");
  const avertissements = result.issues.filter((issue) => issue.severity === "avertissement");
  if (erreurs.length > 0) {
    console.log(`\n${erreurs.length} erreur(s) bloquante(s) :`);
    for (const issue of erreurs.slice(0, 50)) console.log(`  [${issue.facture ?? "-"}] ${issue.message}`);
  }
  if (avertissements.length > 0) {
    console.log(`\n${avertissements.length} avertissement(s) :`);
    for (const issue of avertissements.slice(0, 50)) console.log(`  [${issue.facture ?? "-"}] ${issue.message}`);
  }
  if (erreurs.length > 0) process.exitCode = 2;
}

main().catch((error: unknown) => {
  console.error(error instanceof Error ? error.message : error);
  process.exit(1);
});
