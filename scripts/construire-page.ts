/**
 * Assemble la page : le moteur est compile en un seul script, puis insere
 * dans la page. Le resultat est un fichier autonome, sans reseau ni serveur.
 */
import { build } from "esbuild";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const racine = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const sortie = resolve(racine, "web/dist/passerelle-fne-sage.html");

async function construire() {
  const compile = await build({
    entryPoints: [resolve(racine, "web/app.ts")],
    bundle: true,
    format: "iife",
    target: "es2022",
    minify: true,
    write: false,
    alias: { "@": resolve(racine, "src") },
  });

  const script = compile.outputFiles[0]?.text ?? "";
  const page = await readFile(resolve(racine, "web/page.html"), "utf8");
  // Remplacement par fonction : les sequences comme $& d'un script minifie ne
  // doivent pas etre interpretees par String.replace.
  const assemblee = page.replace("/*BUNDLE*/", () => script);

  await mkdir(dirname(sortie), { recursive: true });
  await writeFile(sortie, assemblee, "utf8");

  const ko = (texte: string) => `${Math.round(texte.length / 1024)} ko`;
  console.log(`Page ecrite : ${sortie}`);
  console.log(`  moteur ${ko(script)}, page ${ko(assemblee)}`);
}

construire();
