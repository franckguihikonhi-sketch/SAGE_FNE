/**
 * La passerelle, cote navigateur. Tout le calcul se fait ici : le fichier
 * depose n'est jamais envoye ailleurs.
 */
import { convertir, ErreurLecture, Resultat } from "@/convertir";
import { UNITES_PAR_DEFAUT } from "@/comptes";
import { decodeText, toBase64 } from "@/cp1252";

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;

interface Reglages {
  depotSage: string;
  clients: string;
  compteDefaut: string;
  numeroPiece: string;
  unites: string;
}

const CHAMPS: Array<keyof Reglages> = [
  "depotSage",
  "clients",
  "compteDefaut",
  "numeroPiece",
  "unites",
];

const DEFAUTS: Reglages = {
  depotSage: "",
  clients: "",
  compteDefaut: "",
  numeroPiece: "vide",
  unites: UNITES_PAR_DEFAUT,
};

const CLE = "passerelle-fne-sage.v2";
const francs = new Intl.NumberFormat("fr-FR", { maximumFractionDigits: 0 });

const etat: { nom: string; texte: string; resultat: Resultat | null } = {
  nom: "",
  texte: "",
  resultat: null,
};

function proteger(valeur: string): string {
  return valeur.replace(
    /[&<>"]/g,
    (caractere) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[caractere] ?? caractere,
  );
}

// --- Reglages -------------------------------------------------------------

const champ = (nom: keyof Reglages) => $<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>(nom);

function reglages(): Reglages {
  return Object.fromEntries(CHAMPS.map((nom) => [nom, champ(nom).value])) as unknown as Reglages;
}

function ecrireReglages(valeurs: Reglages): void {
  for (const nom of CHAMPS) champ(nom).value = valeurs[nom];
}

function lireReglages(): Reglages {
  try {
    const brut = localStorage.getItem(CLE);
    return brut ? { ...DEFAUTS, ...(JSON.parse(brut) as Partial<Reglages>) } : { ...DEFAUTS };
  } catch {
    return { ...DEFAUTS };
  }
}

function enregistrer(): void {
  let garde = true;
  try {
    localStorage.setItem(CLE, JSON.stringify(reglages()));
  } catch {
    garde = false;
  }
  $("etat-reglages").textContent = garde ? "Réglages gardés sur ce poste" : "Réglages non gardés";
}

// --- Conversion -----------------------------------------------------------

function convertirMaintenant(): void {
  if (etat.texte === "") return;
  const valeurs = reglages();

  try {
    etat.resultat = convertir(etat.texte, etat.nom, {
      reglages: {
        depot: valeurs.depotSage,
        numeroPiece: valeurs.numeroPiece === "reference" ? "reference" : "vide",
      },
      clients: valeurs.clients,
      unites: valeurs.unites,
      compteParDefaut: valeurs.compteDefaut,
    });
    afficher(etat.resultat);
  } catch (erreur) {
    etat.resultat = null;
    const message =
      erreur instanceof ErreurLecture
        ? erreur.message
        : `Lecture impossible : ${erreur instanceof Error ? erreur.message : "erreur inattendue"}.`;
    $("resultat").innerHTML = `<div class="bloc"><div class="verdict bloque">
      <span class="pastille"></span><div><strong>Fichier non reconnu</strong><br>
      <span>${proteger(message)}</span></div></div></div>`;
  }
}

async function charger(fichier: File): Promise<void> {
  etat.nom = fichier.name;
  etat.texte = decodeText(new Uint8Array(await fichier.arrayBuffer()));
  $("etat-fichier").textContent = `${fichier.name} — ${francs.format(fichier.size)} octets`;
  convertirMaintenant();
}

// --- Affichage ------------------------------------------------------------

function afficher(resultat: Resultat): void {
  const erreurs = resultat.anomalies.filter((anomalie) => anomalie.gravite === "erreur");
  const reserves = resultat.anomalies.filter((anomalie) => anomalie.gravite !== "erreur");

  $("resultat").innerHTML = [
    verdict(resultat, erreurs.length, reserves.length),
    chiffres(resultat),
    comptesManquants(resultat),
    anomalies("À corriger", erreurs, "erreur"),
    anomalies("À savoir", reserves, "attention"),
    fichier(resultat),
  ].join("");

  $("telecharger")?.addEventListener("click", telecharger);
  $("appliquer")?.addEventListener("click", appliquerComptes);
}

function verdict(resultat: Resultat, erreurs: number, reserves: number): string {
  const pieces = resultat.factures.length;
  if (pieces === 0) {
    return bandeau("bloque", "Aucune facture lue", "Le fichier ne contient pas de facture reconnue.");
  }
  if (erreurs > 0) {
    return bandeau(
      "bloque",
      `${erreurs} point${erreurs > 1 ? "s" : ""} à corriger`,
      "Le fichier est produit, mais Sage refusera ces pièces en l’état.",
    );
  }
  if (reserves > 0) {
    return bandeau(
      "reserve",
      "Prêt à importer",
      `${reserves} remarque${reserves > 1 ? "s" : ""} à lire avant l’import.`,
    );
  }
  return bandeau("pret", "Prêt à importer", `${pieces} pièce${pieces > 1 ? "s" : ""}, aucun point en attente.`);
}

function bandeau(classe: string, titre: string, detail: string): string {
  return `<div class="verdict ${classe}" style="margin-bottom:16px">
    <span class="pastille"></span>
    <div><strong>${proteger(titre)}</strong><br><span>${proteger(detail)}</span></div>
  </div>`;
}

function chiffres(resultat: Resultat): string {
  const { factures, avoirs, lignes, totalHT, totalTva } = resultat.resume;
  const cases: Array<[string, string]> = [
    ["Factures", String(factures)],
    ["Avoirs", String(avoirs)],
    ["Lignes", String(lignes)],
    ["Total HT", `${francs.format(totalHT)} F`],
    ["TVA", `${francs.format(totalTva)} F`],
  ];
  return `<div class="bloc" style="margin-bottom:16px">
    <h2>Ce qui a été lu <span class="compte">${resultat.source === "json" ? "export JSON" : "factures FNE"}</span></h2>
    <div class="chiffres">${cases
      .map(([titre, valeur]) => `<div class="chiffre"><span>${titre}</span><strong>${valeur}</strong></div>`)
      .join("")}</div>
  </div>`;
}

/**
 * Les comptes tiers sont la premiere cause de rejet, et le seul travail que
 * la passerelle ne peut pas faire seule : on le presente comme une saisie a
 * faire, pas comme une liste d'erreurs.
 */
function comptesManquants(resultat: Resultat): string {
  if (resultat.clientsInconnus.length === 0) return "";
  return `<div class="bloc" style="margin-bottom:16px">
    <h2>Comptes tiers à renseigner <span class="compte">${resultat.clientsInconnus.length}</span></h2>
    <p class="aide">Le compte saisi ici est gardé pour les prochaines conversions.</p>
    <div class="large"><table>
      <thead><tr><th>Client</th><th>NCC</th><th class="nombre">Pièces</th><th>Compte Sage</th></tr></thead>
      <tbody>${resultat.clientsInconnus
        .map(
          (client, rang) => `<tr>
            <td>${proteger(client.nom) || "<em>sans nom</em>"}</td>
            <td class="mono">${proteger(client.ncc) || "—"}</td>
            <td class="nombre">${client.factures}</td>
            <td><input type="text" data-compte="${rang}" placeholder="4111..."></td>
          </tr>`,
        )
        .join("")}</tbody>
    </table></div>
    <div class="actions"><button type="button" id="appliquer">Enregistrer ces comptes</button></div>
  </div>`;
}

function anomalies(titre: string, liste: Resultat["anomalies"], classe: string): string {
  if (liste.length === 0) return "";
  const visibles = liste.slice(0, 12);
  return `<div class="bloc" style="margin-bottom:16px">
    <h2>${titre} <span class="compte">${liste.length}</span></h2>
    <ul class="liste">
      ${visibles.map((anomalie) => `<li class="${classe}">${proteger(anomalie.message)}</li>`).join("")}
      ${liste.length > visibles.length ? `<li>… et ${liste.length - visibles.length} autres</li>` : ""}
    </ul>
  </div>`;
}

function fichier(resultat: Resultat): string {
  const lignes = resultat.fichier.texte.split("\r\n").filter(Boolean);
  const apercu = lignes.slice(0, 12).join("\n");
  return `<div class="bloc">
    <h2>Fichier d’import <span class="compte">${resultat.fichier.enregistrements} enregistrements</span></h2>
    <pre class="apercu">${proteger(apercu)}${lignes.length > 12 ? `\n… ${lignes.length - 12} de plus` : ""}</pre>
    <div class="actions">
      <button type="button" id="telecharger">Télécharger ${proteger(resultat.fichier.nom)}</button>
    </div>
    <p class="aide">Dans Sage : <em>Fichier → Importer → Format paramétrable</em>, puis le format du dossier.</p>
  </div>`;
}

// --- Actions --------------------------------------------------------------

function telecharger(): void {
  if (!etat.resultat) return;
  const { nom, octets } = etat.resultat.fichier;
  const lien = document.createElement("a");
  lien.href = `data:text/plain;base64,${toBase64(octets)}`;
  lien.download = nom;
  document.body.appendChild(lien);
  lien.click();
  lien.remove();
}

/** Ajoute les comptes saisis a la table, en remplacant ceux du meme client. */
function appliquerComptes(): void {
  if (!etat.resultat) return;
  const saisies = [...document.querySelectorAll<HTMLInputElement>("input[data-compte]")]
    .map((entree) => ({
      client: etat.resultat!.clientsInconnus[Number(entree.dataset.compte)],
      compte: entree.value.trim(),
    }))
    .filter((saisie) => saisie.client !== undefined && saisie.compte !== "");

  if (saisies.length === 0) return;

  const identifiant = (ligne: string) => (ligne.split(/[;,\t=]/)[0] ?? "").trim().toUpperCase();
  const nouvelles = saisies.map(({ client, compte }) => `${client!.ncc || client!.nom};${compte}`);
  const gardees = champ("clients")
    .value.split(/\r?\n/)
    .filter((ligne) => ligne.trim() !== "")
    .filter((ligne) => !nouvelles.some((ajout) => identifiant(ajout) === identifiant(ligne)));

  champ("clients").value = [...gardees, ...nouvelles].join("\n");
  enregistrer();
  convertirMaintenant();
}

// --- Mise en place --------------------------------------------------------

function demarrer(): void {
  ecrireReglages(lireReglages());
  enregistrer();

  let minuteur: ReturnType<typeof setTimeout> | undefined;
  const appliquer = () => {
    clearTimeout(minuteur);
    enregistrer();
    convertirMaintenant();
  };

  for (const nom of CHAMPS) {
    const element = champ(nom);
    // `change` n'arrive qu'en quittant le champ : on reagit aussi a la frappe,
    // apres une courte pause, sinon rien ne bouge pendant qu'on tape.
    element.addEventListener("input", () => {
      clearTimeout(minuteur);
      minuteur = setTimeout(appliquer, 400);
    });
    element.addEventListener("change", appliquer);
  }

  const entree = $<HTMLInputElement>("fichier");
  entree.addEventListener("change", () => {
    const fichierChoisi = entree.files?.[0];
    if (fichierChoisi) void charger(fichierChoisi);
  });

  const depot = $("depot");
  for (const evenement of ["dragenter", "dragover", "dragleave", "drop"]) {
    depot.addEventListener(evenement, (element) => {
      element.preventDefault();
      depot.classList.toggle("survol", evenement === "dragenter" || evenement === "dragover");
    });
  }
  depot.addEventListener("drop", (evenement) => {
    const glisse = (evenement as DragEvent).dataTransfer?.files?.[0];
    if (glisse) void charger(glisse);
  });
  depot.addEventListener("keydown", (evenement) => {
    const touche = (evenement as KeyboardEvent).key;
    if (touche === "Enter" || touche === " ") entree.click();
  });

  $("oublier").addEventListener("click", () => {
    try {
      localStorage.removeItem(CLE);
    } catch {
      // Rien a oublier.
    }
    ecrireReglages({ ...DEFAUTS });
    enregistrer();
    convertirMaintenant();
  });
}

demarrer();
