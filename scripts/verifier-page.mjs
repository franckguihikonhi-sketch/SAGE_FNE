/**
 * Verification de la page construite, dans un vrai navigateur.
 *
 * Les tests couvrent le moteur ; celui-ci couvre ce qu'ils ne voient pas :
 * la page se charge sans erreur, le fichier depose ressort converti, et les
 * reglages survivent a un rechargement.
 */
import { chromium } from "playwright";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

const page = pathToFileURL(resolve("web/dist/passerelle-fne-sage.html")).href;
const factures = resolve("tests/fixtures/factures-fne.md");
const exportJson = resolve("tests/fixtures/fne-natif.json");

// Chromium est deja installe sur la machine : on le pointe directement
// plutot que de laisser Playwright chercher une version qu'il telechargerait.
const navigateur = await chromium.launch({ executablePath: "/opt/pw-browsers/chromium" });
const onglet = await navigateur.newPage();
const erreurs = [];
onglet.on("pageerror", (erreur) => erreurs.push(String(erreur)));
onglet.on("console", (message) => {
  // Les polices Google ne sont pas joignables depuis cette machine ; la page
  // est concue pour tenir sur ses polices de repli, ce n'est pas une erreur.
  const reseau = /ERR_(CONNECTION|NAME|INTERNET|NETWORK)/.test(message.text());
  if (message.type() === "error" && !reseau) erreurs.push(message.text());
});

const attendre = async (selecteur) => onglet.waitForSelector(selecteur, { timeout: 5000 });
const dire = (message) => console.log(`  ${message}`);

await onglet.goto(page);
await onglet.evaluate(() => localStorage.clear());
await onglet.reload();

// 1. Les factures deposees ressortent converties.
await onglet.setInputFiles("#fichier", factures);
await attendre("#telecharger");
const chiffres = await onglet.textContent(".chiffres");
if (!chiffres.includes("2") || !chiffres.includes("1")) throw new Error("resume absent");
dire(`factures FNE : ${(await onglet.textContent(".verdict strong")).trim()}`);

// 2. Sans compte tiers, la passerelle le reclame plutot que d'inventer.
await attendre("#appliquer");
dire("comptes tiers manquants signales");

// 3. Les comptes saisis dans le tableau nourrissent la table des reglages.
const saisies = await onglet.$$("input[data-compte]");
for (const [rang, saisie] of saisies.entries()) await saisie.fill(`411ESSAI${rang}`);
await onglet.click("#appliquer");
await onglet.waitForFunction(() => !document.getElementById("appliquer"));
const verdict = (await onglet.textContent(".verdict strong")).trim();
if (verdict !== "Prêt à importer") throw new Error(`verdict inattendu : ${verdict}`);
dire("comptes appliques : import pret");

// 4. Le fichier produit a bien quatorze zones.
const zones = await onglet.evaluate(() =>
  document.querySelector("pre.apercu").textContent.split("\n")[0].split("\t").length,
);
if (zones !== 14) throw new Error(`${zones} zones au lieu de 14`);
dire("fichier a 14 zones");

// 5. Les reglages tiennent au rechargement.
await onglet.fill("#depotSage", "DEPOT DE CONTROLE");
await onglet.waitForTimeout(600);
await onglet.reload();
const garde = await onglet.inputValue("#depotSage");
if (garde !== "DEPOT DE CONTROLE") throw new Error("reglages perdus au rechargement");
dire("reglages gardes apres rechargement");

// 6. L'export JSON passe par le meme chemin.
await onglet.setInputFiles("#fichier", exportJson);
await attendre("#telecharger");
if (!(await onglet.textContent(".compte")).includes("JSON")) throw new Error("source JSON non vue");
dire("export JSON reconnu");

// 7. Un fichier etranger est refuse avec une explication.
await onglet.evaluate(() => {
  const donnees = new DataTransfer();
  donnees.items.add(new File(["nom;prenom\nx;y"], "carnet.csv", { type: "text/csv" }));
  const entree = document.getElementById("fichier");
  entree.files = donnees.files;
  entree.dispatchEvent(new Event("change"));
});
await attendre(".verdict.bloque");
dire("fichier etranger refuse avec explication");

await navigateur.close();

if (erreurs.length > 0) {
  console.error("Erreurs JavaScript :");
  for (const erreur of erreurs) console.error(`  ${erreur}`);
  process.exit(1);
}
console.log("Page verifiee, aucune erreur JavaScript.");
