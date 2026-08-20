/**
 * Verification de la page autonome dans un vrai navigateur : la conversion
 * doit aboutir sur les deux formes d'export, sans erreur JavaScript.
 *
 * Necessite Playwright et Chromium : npm install --no-save playwright
 */
import { chromium } from "playwright";
import { mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";

const PAGE = resolve("web/dist/passerelle-fne-sage.html");
const CAS = [
  { fichier: resolve("tests/fixtures/fne-natif.json"), attendu: "ART-001" },
  { fichier: resolve("tests/fixtures/fne-tableau.csv"), attendu: "Facture FNE" },
];

async function main() {
  // La page publiee est enveloppee dans un squelette HTML : on le reproduit ici.
  const dossier = mkdtempSync(join(tmpdir(), "passerelle-"));
  const chemin = join(dossier, "page.html");
  writeFileSync(
    chemin,
    `<!doctype html><html lang="fr"><head><meta charset="utf-8">${readFileSync(PAGE, "utf8")}`,
  );

  const executablePath = process.env.CHROMIUM_PATH ?? "/opt/pw-browsers/chromium";
  const browser = await chromium.launch({ executablePath });
  const page = await browser.newPage({ viewport: { width: 1280, height: 1000 } });

  const erreurs = [];
  page.on("pageerror", (error) => erreurs.push(String(error)));
  page.on("console", (message) => {
    // Les polices Google Fonts sont hors ligne pendant le test : sans consequence.
    if (message.type() === "error" && !message.text().includes("Failed to load resource")) {
      erreurs.push(message.text());
    }
  });

  await page.goto(`file://${chemin}`);
  await page.fill("#compteDefaut", "411DIVERS");
  await page.fill("#depot", "DEPOT PRINCIPAL");

  for (const cas of CAS) {
    await page.setInputFiles("#fichier", cas.fichier);
    await page.waitForSelector("#apercu", { timeout: 15000 });
    const apercu = (await page.textContent("#apercu")) ?? "";
    if (!apercu.includes(cas.attendu)) {
      throw new Error(`${cas.fichier} : "${cas.attendu}" absent du fichier genere.`);
    }
    const stats = (await page.textContent(".stats"))?.replace(/\s+/g, " ").trim();
    console.log(`  ${cas.fichier.split("/").pop()} : ${stats}`);
  }

  await browser.close();
  if (erreurs.length > 0) {
    console.error("Erreurs JavaScript :", erreurs);
    process.exit(1);
  }
  console.log("Page verifiee, aucune erreur JavaScript.");
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
