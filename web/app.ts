/**
 * Demonstration web autonome : tout le moteur de conversion tourne dans le
 * navigateur, aucun fichier n'est envoye sur un serveur.
 */
import { convert, type ConvertResult } from "@/lib/pipeline";
import { readSourceBrowser } from "@/lib/browser/read";
import { parseCustomerMappingCsv } from "@/lib/sage/customers";
import { parsePaymentMappingText } from "@/lib/fne/paiement";
import { PROFILES } from "@/lib/sage/profile";

declare global {
  interface Window {
    claude?: { use?: (name: string) => Promise<unknown> };
  }
}

const $ = <T extends HTMLElement>(id: string): T => document.getElementById(id) as T;

const state: { result: ConvertResult | null; filename: string } = { result: null, filename: "" };

const money = new Intl.NumberFormat("fr-FR", { maximumFractionDigits: 2 });

function option(id: string): string {
  return ($(id) as HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement).value.trim();
}

async function lancer(file: File): Promise<void> {
  const zone = $("resultat");
  zone.innerHTML = `<p class="attente">Conversion de ${escape(file.name)}…</p>`;

  try {
    const bytes = new Uint8Array(await file.arrayBuffer());
    const numero = option("numeroPiece");
    const result = await convert(bytes, file.name, {
      reader: readSourceBrowser,
      profileId: option("profil"),
      customers: parseCustomerMappingCsv(option("clients")),
      customerOptions: { compteParDefaut: option("compteDefaut"), utiliserCodeSource: true },
      reglements: parsePaymentMappingText(option("reglements")),
      parametres: { depot: option("depot"), souche: option("souche") || "1" },
      normalizeOptions:
        numero === "reference" || numero === "vide" ? { numeroPiece: numero } : {},
    });

    state.result = result;
    state.filename = file.name;
    afficher(result);
  } catch (error) {
    zone.innerHTML = `<div class="alerte erreur"><strong>Lecture impossible.</strong> ${escape(
      error instanceof Error ? error.message : String(error),
    )}</div>`;
  }
}

function escape(value: string): string {
  return value.replace(
    /[&<>"]/g,
    (char) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[char] ?? char,
  );
}

function afficher(result: ConvertResult): void {
  const erreurs = result.issues.filter((issue) => issue.severity === "erreur");
  const avertissements = result.issues.filter((issue) => issue.severity !== "erreur");

  const stats = [
    ["Factures", String(result.summary.factures)],
    ["Avoirs", String(result.summary.avoirs)],
    ["Lignes", String(result.summary.lignes)],
    ["Total TTC", `${money.format(result.summary.totalTTC)} XOF`],
  ];

  const source =
    result.source.kind === "fne-json"
      ? "Export JSON natif FNE — le detail des articles est lu directement."
      : `Tableau ${result.source.format.toUpperCase()} — ${result.source.rowCount} enregistrements.`;

  $("resultat").innerHTML = `
    <div class="bloc">
      <p class="source">${escape(source)}</p>
      ${
        result.source.synthese
          ? `<div class="alerte attention"><strong>Export sans detail des articles.</strong>
             Une ligne de synthese a ete generee par facture. Les factures melangeant plusieurs
             taux de TVA ne peuvent pas etre reconstituees : preferez l'export JSON.</div>`
          : ""
      }
      <div class="stats">${stats
        .map(([label, valeur]) => `<div class="stat"><span>${label}</span><strong>${valeur}</strong></div>`)
        .join("")}</div>
    </div>

    ${listeAnomalies("Erreurs bloquantes", erreurs, "erreur")}
    ${listeAnomalies("Avertissements", avertissements, "attention")}
    ${tableauArticles(result)}

    <div class="bloc">
      <div class="entete-bloc">
        <h2>Fichier d'import Sage</h2>
        <div class="actions">
          <button type="button" id="copier" class="secondaire">Copier</button>
          <button type="button" id="telecharger">Telecharger</button>
        </div>
      </div>
      <p class="source">${escape(result.profile.label)} — ${result.file.lineCount} enregistrement(s),
        encodage Windows-1252.</p>
      <pre id="apercu">${escape(apercu(result.file.content))}</pre>
    </div>
  `;

  $("telecharger").addEventListener("click", () => telecharger(result));
  $("copier").addEventListener("click", () => copier(result));
}

function apercu(content: string): string {
  const lignes = content.split("\r\n").filter(Boolean);
  const visible = lignes.slice(0, 40).join("\n");
  return lignes.length > 40 ? `${visible}\n… ${lignes.length - 40} enregistrement(s) de plus` : visible;
}

function listeAnomalies(
  titre: string,
  issues: ConvertResult["issues"],
  ton: string,
): string {
  if (issues.length === 0) return "";
  const parCode = new Map<string, number>();
  for (const issue of issues) parCode.set(issue.code, (parCode.get(issue.code) ?? 0) + 1);

  return `
    <div class="bloc">
      <h2>${titre} <span class="compte">${issues.length}</span></h2>
      <p class="source">${[...parCode]
        .sort((a, b) => b[1] - a[1])
        .map(([code, nombre]) => `${nombre} × ${escape(code)}`)
        .join(" · ")}</p>
      <ul class="anomalies ${ton}">
        ${issues
          .slice(0, 8)
          .map(
            (issue) =>
              `<li>${issue.facture ? `<code>${escape(issue.facture)}</code> ` : ""}${escape(
                issue.message,
              )}</li>`,
          )
          .join("")}
        ${issues.length > 8 ? `<li class="reste">… et ${issues.length - 8} autres</li>` : ""}
      </ul>
    </div>`;
}

function tableauArticles(result: ConvertResult): string {
  if (result.articles.length === 0) return "";
  return `
    <div class="bloc">
      <h2>TVA par article</h2>
      <p class="source">Le format d'import ne transporte pas la taxe : c'est le regime de la fiche
        article Sage qui s'applique. Un article vu a deux taux differents est signale en rouge.</p>
      <div class="table-large"><table>
        <thead><tr><th>Reference</th><th>Designation</th><th>Taux FNE</th><th>Code</th><th class="droite">Lignes</th></tr></thead>
        <tbody>
          ${result.articles
            .slice(0, 20)
            .map(
              (article) => `<tr class="${article.taux.length > 1 ? "conflit" : ""}">
                <td><code>${escape(article.reference || "—")}</code></td>
                <td>${escape(article.designation)}</td>
                <td>${article.taux.map((taux) => `${taux} %`).join(" / ")}</td>
                <td>${escape(article.codesTaxe.join(", "))}</td>
                <td class="droite">${article.lignes}</td>
              </tr>`,
            )
            .join("")}
        </tbody>
      </table></div>
    </div>`;
}

function octets(result: ConvertResult): Uint8Array {
  const binaire = atob(result.file.base64);
  const bytes = new Uint8Array(binaire.length);
  for (let i = 0; i < binaire.length; i += 1) bytes[i] = binaire.charCodeAt(i);
  return bytes;
}

async function telecharger(result: ConvertResult): Promise<void> {
  const bouton = $("telecharger") as HTMLButtonElement;
  // La visionneuse d'artefact bloque les telechargements que la page declenche
  // elle-meme : seule la capacite `downloads` permet de proposer un fichier.
  const downloads = (await window.claude?.use?.("downloads")) as
    | { save: (input: { filename: string; data: Uint8Array }) => Promise<unknown> }
    | null
    | undefined;

  if (!downloads) {
    bouton.textContent = "Indisponible ici — utilisez Copier";
    return;
  }

  try {
    await downloads.save({ filename: result.file.filename, data: octets(result) });
    bouton.textContent = "Enregistre";
  } catch {
    bouton.textContent = "Annule";
  }
  setTimeout(() => (bouton.textContent = "Telecharger"), 2500);
}

async function copier(result: ConvertResult): Promise<void> {
  const bouton = $("copier") as HTMLButtonElement;
  try {
    await navigator.clipboard.writeText(result.file.content);
    bouton.textContent = "Copie";
  } catch {
    bouton.textContent = "Echec";
  }
  setTimeout(() => (bouton.textContent = "Copier"), 2000);
}

function init(): void {
  const select = $("profil") as HTMLSelectElement;
  select.innerHTML = PROFILES.map(
    (profile) => `<option value="${profile.id}">${escape(profile.label)}</option>`,
  ).join("");

  const input = $("fichier") as HTMLInputElement;
  input.addEventListener("change", () => {
    const file = input.files?.[0];
    if (file) void lancer(file);
  });

  const zone = $("depot-fichier");
  for (const evenement of ["dragenter", "dragover", "dragleave", "drop"]) {
    zone.addEventListener(evenement, (event) => {
      event.preventDefault();
      zone.classList.toggle("survol", evenement === "dragenter" || evenement === "dragover");
    });
  }
  zone.addEventListener("drop", (event) => {
    const file = (event as DragEvent).dataTransfer?.files?.[0];
    if (file) void lancer(file);
  });

  // Le depot est un label : au clavier, Entree et Espace doivent ouvrir le selecteur.
  zone.addEventListener("keydown", (event) => {
    const touche = (event as KeyboardEvent).key;
    if (touche === "Enter" || touche === " ") {
      event.preventDefault();
      input.click();
    }
  });

  $("relancer").addEventListener("click", () => {
    const file = input.files?.[0];
    if (file) void lancer(file);
  });
}

init();
