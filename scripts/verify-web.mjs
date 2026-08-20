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
    await page.waitForSelector(".verdict", { timeout: 15000 });
    const apercu = (await page.textContent("pre")) ?? "";
    if (!apercu.includes(cas.attendu)) {
      throw new Error(`${cas.fichier} : "${cas.attendu}" absent du fichier genere.`);
    }
    const stats = (await page.textContent(".stats"))?.replace(/\s+/g, " ").trim();
    console.log(`  ${cas.fichier.split("/").pop()} : ${stats}`);
  }

  // Le PDF de facture certifiee doit etre refuse avec son explication.
  await page.setInputFiles("#fichier", resolve("tests/fixtures/facture.pdf"));
  await page.waitForSelector(".alerte.erreur", { timeout: 8000 });
  const refus = (await page.textContent(".alerte.erreur")) ?? "";
  if (!refus.includes("arrondis au franc")) throw new Error("Le refus du PDF n'est pas explique.");
  console.log("  facture.pdf : refuse avec explication");

  // Affectation d'un compte tiers depuis l'ecran, puis memorisation.
  // Sans compte par defaut, les clients de l'export remontent comme a affecter.
  await page.fill("#compteDefaut", "");
  await page.fill("#clients", "");
  await page.setInputFiles("#fichier", CAS[0].fichier);
  await page.waitForSelector(".compte-client", { timeout: 15000 });
  const clients = await page.locator(".compte-client").count();
  for (let i = 0; i < clients; i += 1) {
    await page.locator(".compte-client").nth(i).fill(`411TEST${i}`);
  }
  await page.click("#appliquer-clients");
  await page.waitForSelector(".verdict.ok", { timeout: 15000 });
  if ((await page.locator(".compte-client").count()) !== 0) {
    throw new Error("Des clients restent sans compte tiers apres affectation.");
  }
  console.log(`  affectation de ${clients} compte(s) tiers : import pret`);

  // Un parametre saisi au clavier doit s'appliquer sans quitter le champ :
  // `change` seul ne se declenche qu'au blur, et l'ecran semblerait fige.
  await page.fill("#clients", "");
  await page.setInputFiles("#fichier", CAS[0].fichier);
  await page.waitForSelector(".compte-client", { timeout: 15000 });
  await page.locator("#compteDefaut").pressSequentially("411DIVERS", { delay: 40 });
  await page.waitForSelector(".verdict.ok", { timeout: 15000 });
  if ((await page.locator(".compte-client").count()) !== 0) {
    throw new Error("Le compte par defaut saisi au clavier n'est pas applique.");
  }
  console.log("  compte par defaut applique sans quitter le champ");

  // Les reglages doivent survivre au rechargement de la page.
  await page.reload();
  if ((await page.inputValue("#depot")) !== "DEPOT PRINCIPAL") {
    throw new Error("Le depot n'est pas conserve.");
  }
  if ((await page.inputValue("#compteDefaut")) !== "411DIVERS") {
    throw new Error("Le compte par defaut n'est pas conserve.");
  }
  console.log("  reglages conserves apres rechargement");

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
