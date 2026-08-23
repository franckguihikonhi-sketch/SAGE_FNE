/**
 * Passerelle FNE vers Sage : tout le moteur de conversion tourne dans le
 * navigateur, aucun fichier n'est envoye sur un serveur.
 */
import { convert, type ConvertResult } from "@/lib/pipeline";
import { readSourceBrowser } from "@/lib/browser/read";
import {
  ecrireReglages,
  fusionnerArticles,
  fusionnerClients,
  lireReglages,
  oublierReglages,
  REGLAGES_PAR_DEFAUT,
  type Reglages,
} from "@/lib/browser/reglages";
import { parseCustomerMappingCsv } from "@/lib/sage/customers";
import { parseArticleMappingCsv } from "@/lib/sage/articles";
import { parsePaymentMappingText } from "@/lib/fne/paiement";
import { PROFILES } from "@/lib/sage/profile";

declare global {
  interface Window {
    claude?: { use?: (name: string) => Promise<unknown> };
  }
}

const $ = <T extends HTMLElement>(id: string): T => document.getElementById(id) as T;

const CHAMPS: Array<keyof Reglages> = [
  "profil",
  "depot",
  "souche",
  "numeroPiece",
  "typeFacture",
  "typeAvoir",
  "compteDefaut",
  "articles",
  "articleSynthese",
  "articleSyntheseExonere",
  "colonnes",
  "colonnesComplement",
  "clients",
  "reglements",
];

const money = new Intl.NumberFormat("fr-FR", { maximumFractionDigits: 2 });
// Colonnes de montants : deux decimales imposees, sinon les chiffres ne
// s'alignent plus d'une ligne a l'autre.
const montant = new Intl.NumberFormat("fr-FR", {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});

const state: { fichier: File | null; resultat: ConvertResult | null } = {
  fichier: null,
  resultat: null,
};

function escape(value: string): string {
  return value.replace(
    /[&<>"]/g,
    (char) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[char] ?? char,
  );
}

// --- Reglages -------------------------------------------------------------

function champ(nom: keyof Reglages): HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement {
  return $(nom);
}

function reglagesCourants(): Reglages {
  return Object.fromEntries(CHAMPS.map((nom) => [nom, champ(nom).value])) as unknown as Reglages;
}

function appliquerReglages(reglages: Reglages): void {
  for (const nom of CHAMPS) champ(nom).value = reglages[nom];
}

let memoire = true;

function enregistrer(): void {
  memoire = ecrireReglages(reglagesCourants());
  $("etat-reglages").textContent = memoire
    ? "Reglages conserves sur ce poste"
    : "Reglages non conserves : stockage du navigateur indisponible";
}

// --- Conversion -----------------------------------------------------------

async function convertir(fichier: File): Promise<void> {
  state.fichier = fichier;
  $("resultat").innerHTML = `<div class="bloc"><p class="attente">Conversion de ${escape(
    fichier.name,
  )}&hellip;</p></div>`;

  const reglages = reglagesCourants();
  try {
    const bytes = new Uint8Array(await fichier.arrayBuffer());
    state.resultat = await convert(bytes, fichier.name, {
      reader: readSourceBrowser,
      profileId: reglages.profil,
      customers: parseCustomerMappingCsv(reglages.clients),
      articles: parseArticleMappingCsv(reglages.articles),
      customerOptions: { compteParDefaut: reglages.compteDefaut, utiliserCodeSource: true },
      reglements: parsePaymentMappingText(reglages.reglements),
      colonnes: {
        retenues: reglages.colonnes,
        complement: reglages.colonnesComplement !== "non",
      },
      parametres: {
        depot: reglages.depot,
        souche: reglages.souche || "1",
        typeFacture: reglages.typeFacture,
        typeAvoir: reglages.typeAvoir,
      },
      normalizeOptions: {
        articleSynthese: reglages.articleSynthese,
        articleSyntheseExonere: reglages.articleSyntheseExonere,
        ...(reglages.numeroPiece === "reference" || reglages.numeroPiece === "vide"
          ? { numeroPiece: reglages.numeroPiece }
          : {}),
      },
    });
    afficher(state.resultat, fichier.name);
  } catch (error) {
    state.resultat = null;
    $("resultat").innerHTML = `<div class="bloc"><div class="alerte erreur">
      <strong>Lecture impossible.</strong> ${escape(
        error instanceof Error ? error.message : String(error),
      )}</div></div>`;
  }
}

function relancer(): void {
  if (state.fichier) void convertir(state.fichier);
}

// --- Affichage ------------------------------------------------------------

function afficher(result: ConvertResult, nomFichier: string): void {
  const erreurs = result.issues.filter((issue) => issue.severity === "erreur");
  const avertissements = result.issues.filter((issue) => issue.severity !== "erreur");
  // Le panneau des comptes tiers traite deja ces anomalies : les relister
  // au-dessous ne ferait que repeter le meme travail.
  const restantes =
    result.clientsInconnus.length > 0
      ? erreurs.filter((issue) => issue.code !== "COMPTE_TIERS_MANQUANT")
      : erreurs;

  $("resultat").innerHTML = `
    ${verdict(result, restantes.length, avertissements.length, nomFichier)}
    ${resume(result)}
    ${clientsInconnus(result)}
    ${articlesInconnus(result)}
    ${listeAnomalies("Anomalies bloquantes", restantes, "erreur")}
    ${listeAnomalies("Avertissements", avertissements, "attention")}
    ${tableauReconstitutions(result)}
    ${tableauArticles(result)}
    ${fichierGenere(result)}
  `;

  $("telecharger")?.addEventListener("click", () => telecharger(result));
  $("copier")?.addEventListener("click", () => copier(result));
  $("appliquer-clients")?.addEventListener("click", appliquerComptes);
  $("appliquer-articles")?.addEventListener("click", appliquerArticles);
}

/**
 * Le verdict distingue trois situations, la deuxieme etant la plus frequente a
 * la premiere utilisation : le fichier est bon, c'est le parametrage du poste
 * qui n'est pas encore fait. L'annoncer comme une anomalie du fichier serait
 * faux et decourageant.
 *
 * Les nombres annonces sont ceux que l'ecran montre : des clients distincts a
 * affecter, et des anomalies restantes - jamais un total qui ne correspond a
 * aucune des listes affichees.
 */
function verdict(
  result: ConvertResult,
  anomalies: number,
  avertissements: number,
  nomFichier: string,
): string {
  const comptes = result.clientsInconnus.length;
  const pluriel = (nombre: number) => (nombre > 1 ? "s" : "");

  let ton: string;
  let titre: string;
  let detail: string;

  if (anomalies === 0 && comptes === 0) {
    ton = "ok";
    titre = "Pret a importer dans Sage";
    detail =
      avertissements > 0
        ? `${avertissements} point${pluriel(avertissements)} de vigilance ci-dessous.`
        : "Aucune anomalie detectee.";
  } else if (anomalies === 0) {
    ton = "attente";
    titre = "Comptes tiers a renseigner";
    detail =
      `${comptes} client${pluriel(comptes)} sans compte tiers Sage : renseignez-les ci-dessous, ` +
      "ou indiquez un compte par defaut a gauche. Le fichier lui-meme est correct.";
  } else {
    ton = "bloque";
    titre = "Import a corriger";
    detail =
      `${anomalies} anomalie${pluriel(anomalies)} dans le fichier` +
      (comptes > 0 ? `, et ${comptes} client${pluriel(comptes)} sans compte tiers.` : ".");
  }

  return `
    <div class="verdict ${ton}">
      <strong>${titre}</strong>
      <span>${escape(nomFichier)} &middot; ${detail}</span>
    </div>`;
}

function resume(result: ConvertResult): string {
  const stats: Array<[string, string]> = [
    ["Factures", String(result.summary.factures)],
    ["Avoirs", String(result.summary.avoirs)],
    ["Lignes", String(result.summary.lignes)],
    ["Total TTC", `${money.format(result.summary.totalTTC)} XOF`],
  ];

  const source =
    result.source.kind === "fne-json"
      ? "Export JSON natif FNE &mdash; le detail des articles est lu directement."
      : `Tableau ${result.source.format.toUpperCase()} &mdash; ${result.source.rowCount} enregistrements` +
        (result.source.colonnesRetenues?.length
          ? `, colonnes ${result.source.colonnesRetenues.join(", ")}.`
          : ".");

  return `
    <div class="bloc">
      <p class="source">${source}</p>
      ${
        result.source.synthese
          ? `<div class="alerte attention"><strong>Export sans detail des articles.</strong>
             Une ligne de synthese a ete generee par facture, a partir des totaux. Les factures
             melangeant plusieurs taux sont reconstituees en une part taxable et une part exoneree,
             deduites du total TVA : verifiez ce partage. L'export JSON, lui, porte le detail reel
             de chaque article.</div>`
          : ""
      }
      <div class="stats">${stats
        .map(([label, valeur]) => `<div class="stat"><span>${label}</span><strong>${valeur}</strong></div>`)
        .join("")}</div>
    </div>`;
}

/**
 * Les clients sans compte tiers sont la premiere cause de blocage d'un import.
 * Plutot que de les lister comme des erreurs, on les presente comme un travail
 * a faire : un compte a saisir par client, memorise pour les fois suivantes.
 */
function clientsInconnus(result: ConvertResult): string {
  if (result.clientsInconnus.length === 0) return "";

  return `
    <div class="bloc">
      <div class="entete-bloc">
        <h2>Comptes tiers a affecter <span class="compte">${result.clientsInconnus.length}</span></h2>
        <div class="actions">
          <button type="button" id="appliquer-clients">Enregistrer et reconvertir</button>
        </div>
      </div>
      <p class="source">
        Saisissez le compte tiers Sage de chaque client. Il sera conserve sur ce poste et
        applique automatiquement aux prochaines conversions.
      </p>
      <div class="table-large">
        <table>
          <thead>
            <tr><th>NCC</th><th>Client</th><th class="droite">Factures</th><th>Compte tiers Sage</th></tr>
          </thead>
          <tbody>
            ${result.clientsInconnus
              .map(
                (client, index) => `<tr>
                  <td><code>${escape(client.ncc || "—")}</code></td>
                  <td>${escape(client.nom)}</td>
                  <td class="droite">${client.factures.length}</td>
                  <td>
                    <input type="text" class="compte-client" data-index="${index}"
                      data-ncc="${escape(client.ncc)}" data-nom="${escape(client.nom)}"
                      placeholder="411..." autocomplete="off">
                  </td>
                </tr>`,
              )
              .join("")}
          </tbody>
        </table>
      </div>
    </div>`;
}

function appliquerComptes(): void {
  const saisies = [...document.querySelectorAll<HTMLInputElement>(".compte-client")]
    .map((entree) => ({
      ncc: entree.dataset.ncc ?? "",
      nom: entree.dataset.nom ?? "",
      compte: entree.value.trim(),
    }))
    .filter((entree) => entree.compte !== "");

  if (saisies.length === 0) {
    $("appliquer-clients").textContent = "Saisissez au moins un compte";
    setTimeout(() => ($("appliquer-clients").textContent = "Enregistrer et reconvertir"), 2000);
    return;
  }

  champ("clients").value = fusionnerClients(champ("clients").value, saisies);
  enregistrer();
  relancer();
}

/**
 * Les references d'article de FNE ne sont pas celles du dossier Sage : la table
 * se complete ici, comme celle des comptes tiers, et se conserve d'une
 * conversion a l'autre.
 */
function articlesInconnus(result: ConvertResult): string {
  if (result.articlesInconnus.length === 0) return "";

  return `
    <div class="bloc">
      <div class="entete-bloc">
        <h2>Articles a faire correspondre <span class="compte">${result.articlesInconnus.length}</span></h2>
        <div class="actions">
          <button type="button" id="appliquer-articles">Enregistrer et reconvertir</button>
        </div>
      </div>
      <p class="source">
        Indiquez la reference de chaque article dans votre dossier Sage. Sans correspondance, la
        reference FNE est transmise telle quelle et Sage risque de refuser la ligne.
      </p>
      <div class="table-large">
        <table>
          <thead>
            <tr><th>Reference FNE</th><th>Designation</th><th class="droite">Lignes</th><th>Reference Sage</th></tr>
          </thead>
          <tbody>
            ${result.articlesInconnus
              .map(
                (article) => `<tr>
                  <td><code>${escape(article.referenceFne)}</code></td>
                  <td>${escape(article.designation)}</td>
                  <td class="droite">${article.lignes}</td>
                  <td>
                    <input type="text" class="reference-article"
                      data-fne="${escape(article.referenceFne)}" placeholder="1147005" autocomplete="off">
                  </td>
                </tr>`,
              )
              .join("")}
          </tbody>
        </table>
      </div>
    </div>`;
}

function appliquerArticles(): void {
  const saisies = [...document.querySelectorAll<HTMLInputElement>(".reference-article")]
    .map((entree) => ({
      referenceFne: entree.dataset.fne ?? "",
      referenceSage: entree.value.trim(),
    }))
    .filter((entree) => entree.referenceSage !== "");

  if (saisies.length === 0) {
    $("appliquer-articles").textContent = "Saisissez au moins une reference";
    setTimeout(() => ($("appliquer-articles").textContent = "Enregistrer et reconvertir"), 2000);
    return;
  }

  champ("articles").value = fusionnerArticles(champ("articles").value, saisies);
  enregistrer();
  relancer();
}

function listeAnomalies(titre: string, issues: ConvertResult["issues"], ton: string): string {
  if (issues.length === 0) return "";
  const parCode = new Map<string, number>();
  for (const issue of issues) parCode.set(issue.code, (parCode.get(issue.code) ?? 0) + 1);

  return `
    <div class="bloc">
      <h2>${titre} <span class="compte">${issues.length}</span></h2>
      <p class="source">${[...parCode]
        .sort((a, b) => b[1] - a[1])
        .map(([code, nombre]) => `${nombre} &times; ${escape(code)}`)
        .join(" &middot; ")}</p>
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
        ${issues.length > 8 ? `<li class="reste">&hellip; et ${issues.length - 8} autres</li>` : ""}
      </ul>
    </div>`;
}

/**
 * Les factures reconstituees se verifient en les comparant entre elles : un
 * tableau trie par part exoneree fait ressortir les cas atypiques, la ou
 * quatorze avertissements identiques ne disent rien.
 */
function tableauReconstitutions(result: ConvertResult): string {
  if (result.reconstitutions.length === 0) return "";
  const nombre = result.reconstitutions.length;
  const lignes = [...result.reconstitutions].sort((a, b) => b.partExoneree - a.partExoneree);

  return `
    <div class="bloc">
      <h2>Factures reconstituees <span class="compte">${nombre}</span></h2>
      <p class="source">
        Ces factures melangent plusieurs taux, que l'export Excel ne detaille pas. La part taxable
        se deduit du total TVA (TVA &divide; 18 %), le reste est exonere : le partage est exact si la
        facture ne melange que le taux normal et des articles exoneres. Verifiez-le sur l'export
        JSON, qui porte le detail reel de chaque article.
      </p>
      <div class="table-large">
        <table>
          <thead>
            <tr>
              <th>Facture</th>
              <th class="droite">Taux effectif</th>
              <th class="droite">Part taxable</th>
              <th class="droite">Part exoneree</th>
              <th class="droite">Exonere</th>
            </tr>
          </thead>
          <tbody>
            ${lignes
              .map(
                (ligne) => `<tr>
                  <td><code>${escape(ligne.reference)}</code></td>
                  <td class="droite">${ligne.tauxEffectif} %</td>
                  <td class="droite">${montant.format(ligne.htTaxable)}</td>
                  <td class="droite">${montant.format(ligne.htExonere)}</td>
                  <td class="droite">${ligne.partExoneree} %</td>
                </tr>`,
              )
              .join("")}
          </tbody>
        </table>
      </div>
    </div>`;
}

function tableauArticles(result: ConvertResult): string {
  if (result.articles.length === 0) return "";
  return `
    <div class="bloc">
      <h2>TVA par article</h2>
      <p class="source">Le format d'import ne transporte pas la taxe : c'est le regime de la fiche
        article Sage qui s'applique. Un article vu a deux taux differents est signale en rouge.</p>
      <div class="table-large">
        <table>
          <thead>
            <tr><th>Reference</th><th>Designation</th><th>Taux FNE</th><th>Code</th><th class="droite">Lignes</th></tr>
          </thead>
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
        </table>
      </div>
    </div>`;
}

function fichierGenere(result: ConvertResult): string {
  return `
    <div class="bloc">
      <div class="entete-bloc">
        <h2>Fichier d'import Sage</h2>
        <div class="actions">
          <button type="button" id="copier" class="secondaire">Copier</button>
          <button type="button" id="telecharger">Telecharger</button>
        </div>
      </div>
      <p class="source">${escape(result.profile.label)} &mdash; ${result.file.lineCount}
        enregistrement(s), encodage Windows-1252.</p>
      <pre>${escape(apercu(result.file.content))}</pre>
    </div>`;
}

function apercu(content: string): string {
  const lignes = content.split("\r\n").filter(Boolean);
  const visible = lignes.slice(0, 40).join("\n");
  return lignes.length > 40
    ? `${visible}\n… ${lignes.length - 40} enregistrement(s) de plus`
    : visible;
}

// --- Sortie de fichiers ---------------------------------------------------

function octets(base64: string): Uint8Array {
  const binaire = atob(base64);
  const bytes = new Uint8Array(binaire.length);
  for (let i = 0; i < binaire.length; i += 1) bytes[i] = binaire.charCodeAt(i);
  return bytes;
}

async function enregistrerFichier(filename: string, data: Uint8Array): Promise<boolean | null> {
  // La visionneuse bloque les telechargements que la page declenche elle-meme :
  // seule la capacite `downloads` permet de proposer un fichier.
  const downloads = (await window.claude?.use?.("downloads")) as
    | { save: (input: { filename: string; data: Uint8Array }) => Promise<unknown> }
    | null
    | undefined;
  if (!downloads) return null;

  try {
    await downloads.save({ filename, data });
    return true;
  } catch {
    return false;
  }
}

function signaler(bouton: HTMLButtonElement, texte: string, initial: string): void {
  bouton.textContent = texte;
  setTimeout(() => (bouton.textContent = initial), 2500);
}

async function telecharger(result: ConvertResult): Promise<void> {
  const bouton = $("telecharger") as HTMLButtonElement;
  const etat = await enregistrerFichier(result.file.filename, octets(result.file.base64));
  if (etat === null) signaler(bouton, "Indisponible ici, utilisez Copier", "Telecharger");
  else signaler(bouton, etat ? "Enregistre" : "Annule", "Telecharger");
}

async function copier(result: ConvertResult): Promise<void> {
  const bouton = $("copier") as HTMLButtonElement;
  try {
    await navigator.clipboard.writeText(result.file.content);
    signaler(bouton, "Copie", "Copier");
  } catch {
    signaler(bouton, "Echec de la copie", "Copier");
  }
}

async function exporterClients(): Promise<void> {
  const bouton = $("exporter-clients") as HTMLButtonElement;
  const texte = champ("clients").value.trim();
  if (!texte) {
    signaler(bouton, "Table vide", "Exporter");
    return;
  }

  const contenu = `ncc;nom;compte\n${texte}\n`;
  const etat = await enregistrerFichier("clients-sage.csv", new TextEncoder().encode(contenu));
  if (etat !== null) {
    signaler(bouton, etat ? "Enregistre" : "Annule", "Exporter");
    return;
  }
  try {
    await navigator.clipboard.writeText(contenu);
    signaler(bouton, "Copie dans le presse-papiers", "Exporter");
  } catch {
    signaler(bouton, "Indisponible", "Exporter");
  }
}

async function importerClients(fichier: File): Promise<void> {
  const texte = await fichier.text();
  champ("clients").value = texte.trim();
  enregistrer();
  relancer();
}

// --- Mise en place --------------------------------------------------------

function init(): void {
  const selecteur = $("profil") as HTMLSelectElement;
  selecteur.innerHTML = PROFILES.map(
    (profile) => `<option value="${profile.id}">${escape(profile.label)}</option>`,
  ).join("");

  appliquerReglages(lireReglages());
  enregistrer();

  // `change` ne se declenche qu'en quittant le champ : un utilisateur qui tape
  // un compte par defaut et regarde l'ecran ne verrait rien se produire.
  // On reagit donc a la frappe, apres une courte pause, et immediatement
  // lorsque le champ est quitte ou qu'une liste deroulante change.
  let minuteur: ReturnType<typeof setTimeout> | undefined;
  const appliquer = () => {
    clearTimeout(minuteur);
    enregistrer();
    relancer();
  };

  for (const nom of CHAMPS) {
    const element = champ(nom);
    element.addEventListener("input", () => {
      clearTimeout(minuteur);
      minuteur = setTimeout(appliquer, 500);
    });
    element.addEventListener("change", appliquer);
  }

  const entree = $("fichier") as HTMLInputElement;
  entree.addEventListener("change", () => {
    const fichier = entree.files?.[0];
    if (fichier) void convertir(fichier);
  });

  const zone = $("depot-fichier");
  for (const evenement of ["dragenter", "dragover", "dragleave", "drop"]) {
    zone.addEventListener(evenement, (event) => {
      event.preventDefault();
      zone.classList.toggle("survol", evenement === "dragenter" || evenement === "dragover");
    });
  }
  zone.addEventListener("drop", (event) => {
    const fichier = (event as DragEvent).dataTransfer?.files?.[0];
    if (fichier) void convertir(fichier);
  });
  // Le depot est un label : au clavier, Entree et Espace ouvrent le selecteur.
  zone.addEventListener("keydown", (event) => {
    const touche = (event as KeyboardEvent).key;
    if (touche === "Enter" || touche === " ") {
      event.preventDefault();
      entree.click();
    }
  });

  const importClients = $("import-clients") as HTMLInputElement;
  importClients.addEventListener("change", () => {
    const fichier = importClients.files?.[0];
    if (fichier) void importerClients(fichier);
  });

  $("exporter-clients").addEventListener("click", () => void exporterClients());

  $("reinitialiser").addEventListener("click", () => {
    oublierReglages();
    appliquerReglages({ ...REGLAGES_PAR_DEFAUT });
    enregistrer();
    relancer();
  });
}

init();
