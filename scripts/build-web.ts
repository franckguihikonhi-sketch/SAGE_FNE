/**
 * Assemble la demonstration web autonome : le moteur de conversion est
 * compile pour le navigateur puis insere dans la page, qui ne depend alors
 * d'aucune ressource externe hormis les polices Google Fonts.
 */
import { build } from "esbuild";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";

const SORTIE = resolve("web/dist/passerelle-fne-sage.html");

async function main() {
  const bundle = await build({
    entryPoints: [resolve("web/app.ts")],
    bundle: true,
    format: "iife",
    target: "es2022",
    minify: true,
    write: false,
    tsconfig: resolve("tsconfig.json"),
  });

  const script = bundle.outputFiles[0]?.text;
  if (!script) throw new Error("La compilation n'a produit aucun fichier.");
  // Une balise de fermeture dans le code casserait le <script> qui l'entoure.
  const sûr = script.replace(/<\/script/gi, "<\\/script");

  // Remplacement par fonction : le code minifie contient des "$&", que la forme
  // chaine de String.replace interpreterait comme des motifs.
  const page = readFileSync(resolve("web/template.html"), "utf8").replace("/*BUNDLE*/", () => sûr);
  mkdirSync(dirname(SORTIE), { recursive: true });
  writeFileSync(SORTIE, page, "utf8");

  const ko = (value: number) => `${(value / 1024).toFixed(0)} ko`;
  console.log(`Page generee : ${SORTIE}`);
  console.log(`  moteur ${ko(script.length)}, page complete ${ko(page.length)}`);
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
