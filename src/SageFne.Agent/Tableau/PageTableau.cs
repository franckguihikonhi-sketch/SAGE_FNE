namespace SageFne.Agent.Tableau;

/// <summary>
/// La page du tableau de bord, en un seul fichier.
/// </summary>
/// <remarks>
/// Aucune ressource extérieure : ni police, ni script, ni feuille de style
/// chargée depuis Internet. Le poste qui fait tourner l'agent est celui d'un
/// cabinet, pas un serveur : il doit pouvoir travailler quand la connexion
/// tombe, et c'est précisément quand la plateforme ne répond pas qu'on a besoin
/// de voir où en sont les factures.
/// </remarks>
public static class PageTableau
{
    public const string Html = """
<!doctype html>
<html lang="fr">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Factures FNE — agent Sage</title>
<style>
  :root {
    --fond: #f6f7f9; --carte: #fff; --trait: #e3e6ea; --texte: #1c2024;
    --doux: #656d76; --accent: #1f6feb; --vert: #1a7f37; --rouge: #c0392b;
    --ambre: #9a6700; --gris: #57606a;
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; background: var(--fond); color: var(--texte);
    font: 14px/1.5 -apple-system, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
  }
  header {
    background: var(--carte); border-bottom: 1px solid var(--trait);
    padding: 14px 20px; position: sticky; top: 0; z-index: 5;
  }
  h1 { font-size: 16px; margin: 0 0 8px; font-weight: 600; }
  .bandeau { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
  .jeton {
    font-size: 12px; padding: 3px 9px; border-radius: 999px;
    border: 1px solid var(--trait); background: #fafbfc; color: var(--gris);
  }
  .jeton b { font-weight: 600; color: var(--texte); }
  .jeton.alerte { background: #fff8f5; border-color: #f5c6ba; color: var(--rouge); }
  .jeton.ok { background: #f2fbf4; border-color: #b7e3c1; color: var(--vert); }
  .jeton.attention { background: #fff9ec; border-color: #ecd39a; color: var(--ambre); }
  main { padding: 20px; max-width: 1280px; margin: 0 auto; }
  .avis {
    background: #fff8f5; border: 1px solid #f5c6ba; color: #8a2c1b;
    padding: 12px 14px; border-radius: 8px; margin-bottom: 16px;
  }
  .avis.calme { background: #f7f9fb; border-color: var(--trait); color: var(--gris); }
  .chiffres { display: flex; gap: 12px; flex-wrap: wrap; margin-bottom: 16px; }
  .chiffre {
    background: var(--carte); border: 1px solid var(--trait); border-radius: 8px;
    padding: 12px 16px; min-width: 132px;
  }
  .chiffre .n { font-size: 22px; font-weight: 600; }
  .chiffre .l { font-size: 12px; color: var(--doux); }
  table { width: 100%; border-collapse: collapse; background: var(--carte);
          border: 1px solid var(--trait); border-radius: 8px; overflow: hidden; }
  th, td { padding: 10px 12px; text-align: left; border-bottom: 1px solid var(--trait);
           vertical-align: top; }
  th { font-size: 12px; text-transform: uppercase; letter-spacing: .04em;
       color: var(--doux); font-weight: 600; background: #fafbfc; }
  tr:last-child td { border-bottom: 0; }
  td.num { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
  .piece { font-weight: 600; }
  .pastille { display: inline-block; font-size: 12px; padding: 2px 8px;
              border-radius: 999px; white-space: nowrap; }
  .p-pret   { background: #ddf4e4; color: #10682a; }
  .p-fait   { background: #dbeafe; color: #1e40af; }
  .p-bloque { background: #fde8e4; color: #a13224; }
  .p-attente{ background: #fdf1d6; color: #8a5a00; }
  .p-neutre { background: #eef1f4; color: var(--gris); }
  .motif { color: var(--doux); font-size: 12.5px; margin-top: 3px; max-width: 52ch; }
  .conduite { color: var(--vert); font-size: 12.5px; font-weight: 600; margin-top: 4px; }
  .codes { margin-top: 4px; display: flex; flex-wrap: wrap; gap: 4px; }
  code { font: 11.5px ui-monospace, Menlo, Consolas, monospace;
         background: #f0f2f4; border-radius: 4px; padding: 1px 5px; color: #6b4b00; }
  code.bloquant { background: #fde8e4; color: #a13224; }
  button {
    font: inherit; font-weight: 600; border-radius: 6px; padding: 6px 13px;
    border: 1px solid var(--accent); background: var(--accent); color: #fff;
    cursor: pointer; white-space: nowrap;
  }
  button:hover:not(:disabled) { filter: brightness(1.08); }
  button:disabled { opacity: .45; cursor: default; }
  button.discret { background: var(--carte); color: var(--gris); border-color: var(--trait); }
  .ref { font: 12px ui-monospace, Menlo, Consolas, monospace; color: var(--vert); }
  .vide { padding: 40px; text-align: center; color: var(--doux); }
  dialog {
    border: 1px solid var(--trait); border-radius: 10px; padding: 0;
    max-width: 460px; box-shadow: 0 12px 40px rgba(0,0,0,.18);
  }
  dialog::backdrop { background: rgba(15,20,25,.42); }
  .d-corps { padding: 20px; }
  .d-corps h2 { margin: 0 0 10px; font-size: 16px; }
  .d-corps p { margin: 0 0 10px; }
  .d-pied { display: flex; gap: 8px; justify-content: flex-end;
            padding: 12px 20px; border-top: 1px solid var(--trait); background: #fafbfc; }
  .grave { color: var(--rouge); font-weight: 600; }
  footer { color: var(--doux); font-size: 12px; padding: 16px 4px 40px; }
  pre.brut {
    background: #f0f2f4; border: 1px solid var(--trait); border-radius: 6px;
    padding: 10px; margin: 0; max-height: 240px; overflow: auto;
    font: 11.5px/1.5 ui-monospace, Menlo, Consolas, monospace; white-space: pre-wrap;
    word-break: break-word;
  }
  select.enligne {
    font: inherit; font-size: 13px; padding: 5px 8px; border-radius: 6px;
    border: 1px solid var(--trait); background: var(--carte); color: var(--texte);
    min-width: 152px; margin-bottom: 6px;
  }
  select.enligne.vide { border-color: #d8b26a; background: #fffdf6; }
  .action { display: flex; flex-direction: column; align-items: flex-end; gap: 2px; }
  select {
    font: inherit; width: 100%; padding: 8px 10px; border-radius: 6px;
    border: 1px solid var(--trait); background: var(--carte); color: var(--texte);
  }
  select:focus { outline: 2px solid var(--accent); outline-offset: 1px; }
  label.champ { display: block; font-weight: 600; margin: 14px 0 6px; }
  .mode { color: var(--doux); font-size: 12px; margin-top: 3px; }
  .mode.suppose { color: var(--ambre); }
  .titre-ok { color: var(--vert); }
  .titre-ko { color: var(--rouge); }
</style>
</head>
<body>
<header>
  <h1>Factures Sage &rarr; certification FNE</h1>
  <div class="bandeau" id="bandeau"><span class="jeton">Chargement…</span></div>
</header>

<main>
  <div id="avis"></div>
  <div class="chiffres" id="chiffres"></div>
  <table>
    <thead>
      <tr>
        <th>Date</th><th>Pièce</th><th>Client</th>
        <th class="num">Total TTC</th><th>État</th><th></th>
      </tr>
    </thead>
    <tbody id="corps">
      <tr><td colspan="6" class="vide">Lecture du dossier Sage…</td></tr>
    </tbody>
  </table>
  <footer id="pied"></footer>
</main>

<dialog id="confirmation">
  <div class="d-corps">
    <h2>Certifier la pièce <span id="c-piece"></span> ?</h2>
    <p>La facture part immédiatement à la DGI, sur <b id="c-env"></b>.</p>
    <p class="grave">Une facture certifiée ne s'annule pas. La seule correction
       possible ensuite est un avoir.</p>
    <p id="c-client" style="color:var(--doux)"></p>

    <p><b>Mode de paiement :</b> <span id="c-mode-libelle"></span><br>
       <span class="mode">Sage ne porte pas cette information : elle part telle quelle
       sur la facture certifiée.</span></p>
  </div>
  <div class="d-pied">
    <button class="discret" id="c-non">Annuler</button>
    <button id="c-oui">Certifier</button>
  </div>
</dialog>

<dialog id="resultat">
  <div class="d-corps">
    <h2 id="r-titre"></h2>
    <p id="r-message"></p>
    <p id="r-brut-libelle" style="color:var(--doux);margin-bottom:6px"></p>
    <pre class="brut" id="r-brut" hidden></pre>
  </div>
  <div class="d-pied"><button id="r-ok">Fermer</button></div>
</dialog>

<script>
const $ = (id) => document.getElementById(id);
const fcfa = (n) => new Intl.NumberFormat('fr-FR').format(Math.round(n)) + ' F';
const jour = (d) => { const p = d.split('-'); return p[2] + '/' + p[1] + '/' + p[0]; };
const html = (s) => String(s ?? '').replace(/[&<>"']/g,
  (c) => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));

let etat = null;
let occupe = null;
let modes = [];

const PASTILLES = {
  ACertifier:     ['p-pret',    'à certifier'],
  DejaCertifiee:  ['p-fait',    'certifiée'],
  Transmise:      ['p-fait',    'au portail'],
  EnSuspens:      ['p-attente', 'envoi en suspens'],
  ModifieeDepuis: ['p-bloque',  'modifiée après certification'],
  Bloquee:        ['p-bloque',  'bloquée'],
  HorsPerimetre:  ['p-neutre',  'hors périmètre'],
};

async function chargerModes() {
  // Les six de la DGI, servis par l'agent depuis le lexique de la procédure —
  // jamais recopiés dans la page. Une liste en double finirait par diverger de
  // ce que l'API accepte, et les factures seraient refusées.
  modes = await fetch('/api/modes-paiement').then((r) => r.json());
}

// Ce que l'exploitant a choisi pendant cette session, par pièce. La liste se
// redessine toutes les quinze secondes : sans cette mémoire, un choix fait dix
// secondes plus tôt disparaîtrait sous les doigts.
const choix = {};

function optionsMode(valeur) {
  return '<option value="">Sélectionner…</option>'
    + modes.map((m) => `<option value="${html(m.code)}"`
        + `${m.code === valeur ? ' selected' : ''}>${html(m.libelle)}</option>`).join('');
}

async function charger() {
  try {
    const [e, f] = await Promise.all([
      fetch('/api/etat').then((r) => r.json()),
      fetch('/api/factures').then((r) => r.json()),
    ]);
    if (verifierBuild(e)) return;

    etat = e;
    dessinerEtat(e);
    dessinerFactures(f);
  } catch (err) {
    $('bandeau').innerHTML = '<span class="jeton alerte">Agent injoignable</span>';
  }
}

// Le binaire a-t-il changé sous nos pieds ? Un onglet resté ouvert pendant une
// republication garde l'ancien code pour toujours : la page rafraîchit ses
// données, jamais elle-même. Deux nouveautés livrées ont été crues absentes
// pour cette seule raison.
let build = null;

function verifierBuild(e) {
  if (!e.build) return false;
  if (build === null) { build = e.build; return false; }
  if (build === e.build) return false;

  // Rechargement plutôt qu'un bandeau « une nouvelle version est disponible » :
  // il n'y a rien à décider, et rien en cours qu'un rechargement ferait perdre —
  // les choix de mode sont réappliqués depuis le serveur.
  location.reload();
  return true;
}

function dessinerEtat(e) {
  const j = [];
  j.push(`<span class="jeton ${e.environnement === 'TEST' ? '' : 'alerte'}">`
       + `Plateforme <b>${html(e.environnement)}</b></span>`);
  j.push(`<span class="jeton">Mode <b>${html(e.mode)}</b></span>`);
  j.push(`<span class="jeton ${e.plateformeJoignable ? 'ok' : 'alerte'}">`
       + (e.plateformeJoignable ? 'DGI joignable' : 'DGI injoignable') + '</span>');
  if (!e.surDonneesReelles) {
    j.push('<span class="jeton alerte">JEU D\'ESSAI — factures inventées</span>');
  }
  j.push(`<span class="jeton ${e.identiteRenseignee ? '' : 'alerte'}">`
       + `Etab. <b>${html(e.etablissement || '—')}</b></span>`);
  j.push(`<span class="jeton">Fenêtre <b>${e.fenetreJours} j</b></span>`);
  j.push(`<span class="jeton">Lu à ${html(e.lu)}</span>`);
  $('bandeau').innerHTML = j.join('');

  // Un avis porte sa gravité : une facture inventée ou une plateforme muette
  // sont des alertes ; « rien à faire pour l'instant » est une information.
  // Tout peindre en rouge fait qu'on ne lit plus rien.
  const avis = [];
  const alerter = (texte) => avis.push({ texte, calme: false });
  const informer = (texte) => avis.push({ texte, calme: true });

  if (!e.surDonneesReelles) {
    alerter("L'agent ne lit pas votre dossier Sage mais un jeu d'essai. "
      + "Rien de ce qui s'affiche ici n'est réel, et rien ne peut être certifié.");
  }
  if (e.environnement === 'PRODUCTION') {
    alerter('La plateforme est en PRODUCTION : chaque certification est un acte fiscal réel.');
  }
  if (!e.plateformeJoignable) {
    alerter('La plateforme DGI ne répond pas : ' + html(e.plateformeExplication));
  }
  if (!e.identiteRenseignee) {
    // Sans eux, la DGI refuse TOUTES les factures — « Establishment is
    // invalid ». Ils ne viennent pas de Sage : aucun contrôle de pièce ne peut
    // les voir, et une facture irréprochable part quand même se faire refuser.
    alerter('<b>Aucune facture ne peut être certifiée.</b> L\'identité du dossier '
      + 'auprès de la DGI n\'est pas renseignée — point de vente « '
      + html(e.pointDeVente) + ' », établissement « ' + html(e.etablissement) + ' ». '
      + 'Ces valeurs vous sont données par la DGI avec votre accès à la plateforme ; '
      + 'elles ne viennent pas de Sage.');
  }
  // Une liste où rien n'est à faire, sans un mot pour dire pourquoi. C'est ce
  // qu'a vu l'exploitant en cherchant en vain le menu du mode de règlement : il
  // ne s'affiche que sur les lignes certifiables, et il n'y en avait aucune.
  // « 0 prêtes à certifier » était bien à l'écran, dans un compteur — mais un
  // compteur à zéro ne dit pas ce qu'il faudrait faire pour qu'il monte.
  if (e.total > 0 && e.certifiables === 0) {
    const parts = [];
    if (e.certifiees) parts.push(`${e.certifiees} déjà certifiée(s)`);
    if (e.bloquees) parts.push(`${e.bloquees} bloquée(s)`);
    const reste = e.total - e.certifiees - e.bloquees;
    if (reste > 0) parts.push(`${reste} au portail, en suspens ou hors périmètre`);

    informer('<b>Aucune facture n\'attend d\'être certifiée.</b> Sur les '
      + `${e.total} lue(s) sur la fenêtre : ${parts.join(', ')}. `
      + 'Le menu du mode de règlement et le bouton n\'apparaissent que sur une '
      + 'ligne prête à partir — il n\'y en a aucune. '
      + (e.demarrageLe
          ? `Seules les factures datées du ${jour(e.demarrageLe)} ou après sont candidates.`
          : ''));
  }

  $('avis').innerHTML = avis
    .map((a) => `<div class="avis${a.calme ? ' calme' : ''}">${a.texte}</div>`).join('');

  $('chiffres').innerHTML = [
    ['Lues sur la fenêtre', e.total],
    ['Prêtes à certifier', e.certifiables],
    ['Déjà certifiées', e.certifiees],
    ['Bloquées', e.bloquees],
  ].map(([l, n]) => `<div class="chiffre"><div class="n">${n}</div>`
                  + `<div class="l">${l}</div></div>`).join('');
}

function dessinerFactures(lignes) {
  if (!lignes.length) {
    $('corps').innerHTML = '<tr><td colspan="6" class="vide">'
      + 'Aucune facture sur la fenêtre. Élargissez <code>Agent:FenetreJours</code> '
      + 'pour en lire davantage.</td></tr>';
    $('pied').textContent = '';
    return;
  }

  $('corps').innerHTML = lignes.map((l) => {
    const [classe, libelle] = PASTILLES[l.etat] || ['p-neutre', l.libelleEtat];
    const codes = l.constats.map((c) =>
      `<code class="${c.bloquant ? 'bloquant' : ''}" title="${html(c.message)}">`
      + `${html(c.code)}</code>`).join('');

    // Le mode se choisit AVANT le bouton, sur la ligne : c'est là qu'on regarde
    // la facture. Il était d'abord dans la fenêtre de confirmation, donc
    // invisible tant qu'on n'avait pas cliqué — l'inverse de ce qui était
    // demandé, et rien ne se déroulait sur la liste.
    //
    // Présélectionné seulement s'il a déjà été choisi pour ce client : un mode
    // venu du paramétrage n'est pas un choix, et le présélectionner ferait
    // passer une supposition pour une décision.
    const retenu = choix[l.piece]
      ?? (l.modePaiementChoisi ? l.modePaiement : '');

    const action = l.certifiable
      ? `<div class="action">
           <select class="enligne ${retenu ? '' : 'vide'}" data-mode="${html(l.piece)}"
                   title="Mode de paiement — obligatoire">${optionsMode(retenu)}</select>
           <button data-piece="${html(l.piece)}"
                   ${occupe || !retenu ? 'disabled' : ''}>Certifier</button>
         </div>`
      : (l.referenceFne
          ? `<span class="ref">${html(l.referenceFne)}</span>`
          : '');

    // Deux faits distincts, et les confondre a déjà trompé : ce que VOUS pouvez
    // faire maintenant, et ce que l'agent ferait tout seul. Une pièce prête
    // affichait « il reste 277 s de stabilité » à côté d'un bouton actif — on ne
    // savait plus si cliquer était prématuré. Ça ne l'est pas : le délai de
    // stabilité remplace le jugement humain, il ne s'y ajoute pas.
    const conduite = l.certifiable
      ? `<div class="conduite">Vous pouvez la certifier maintenant.</div>`
      : '';

    // Ce qui partira réellement — mais seulement là où aucune liste ne le dit
    // déjà. Sur une ligne certifiable, la liste EST l'affirmation du mode ;
    // répéter à côté « À terme — supposé » pendant qu'elle affiche « Mobile
    // money » ferait deux affirmations contraires sur le même fait, à trois
    // centimètres l'une de l'autre.
    const mode = l.certifiable ? '' :
      `<div class="mode ${l.modePaiementChoisi ? '' : 'suppose'}">`
      + `Paiement : ${html(l.modePaiementLibelle)}`
      + (l.modePaiementChoisi ? '' : ' — supposé, non choisi') + '</div>';

    return `<tr>
      <td>${jour(l.date)}</td>
      <td class="piece">${html(l.piece)}</td>
      <td>${html(l.clientNom || l.client)}
          <div class="motif">${html(l.client)}${l.clientNcc ? ' · NCC ' + html(l.clientNcc) : ''}</div></td>
      <td class="num">${fcfa(l.totalTTC)}</td>
      <td><span class="pastille ${classe}">${html(libelle)}</span>
          ${conduite}
          ${mode}
          <div class="motif">${l.certifiable ? 'L\'agent, seul&nbsp;: ' : ''}${html(l.explication)}</div>
          ${codes ? `<div class="codes">${codes}</div>` : ''}</td>
      <td class="num">${action}</td>
    </tr>`;
  }).join('');

  $('corps').querySelectorAll('select[data-mode]').forEach((s) =>
    s.addEventListener('change', () => {
      const piece = s.dataset.mode;
      choix[piece] = s.value;
      s.classList.toggle('vide', !s.value);

      const bouton = $('corps').querySelector(`button[data-piece="${CSS.escape(piece)}"]`);
      if (bouton) bouton.disabled = !s.value || !!occupe;
    }));

  $('corps').querySelectorAll('button[data-piece]').forEach((b) =>
    b.addEventListener('click', () => demander(b.dataset.piece, lignes)));

  $('pied').textContent = lignes.length + ' facture(s) affichée(s). '
    + 'La liste se rafraîchit toute seule toutes les 15 secondes.';
}

function demander(piece, lignes) {
  const ligne = lignes.find((l) => l.piece === piece);
  $('c-piece').textContent = piece;
  $('c-env').textContent = etat ? etat.environnement : '?';
  $('c-client').textContent = ligne
    ? `${ligne.clientNom || ligne.client} — ${fcfa(ligne.totalTTC)}` : '';

  // Le mode a déjà été choisi sur la ligne : la fenêtre le rappelle, elle ne le
  // redemande pas. Deux endroits pour un même choix, c'est deux valeurs qui
  // finissent par différer.
  const modeChoisi = choix[piece]
    ?? (ligne && ligne.modePaiementChoisi ? ligne.modePaiement : '');

  if (!modeChoisi) return;

  const libelle = (modes.find((m) => m.code === modeChoisi) || {}).libelle || modeChoisi;
  $('c-mode-libelle').textContent = libelle;

  $('confirmation').returnValue = '';
  $('confirmation').showModal();
  $('c-oui').onclick = () => {
    $('confirmation').close();
    certifier(piece, modeChoisi);
  };
}

$('c-non').onclick = () => $('confirmation').close();

async function certifier(piece, modePaiement) {
  occupe = piece;
  document.querySelectorAll('button[data-piece]').forEach((b) => (b.disabled = true));
  try {
    const r = await fetch(`/api/factures/${encodeURIComponent(piece)}/certifier`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ modePaiement }),
    });
    const d = await r.json();
    montrer(d);
  } catch (err) {
    montrer({
      reussi: false,
      message: "L'agent n'a pas répondu. Regardez le journal : la facture est peut-être "
             + 'partie malgré tout, et dans ce cas elle ne doit pas être renvoyée.',
    });
  } finally {
    occupe = null;
    charger();
  }
}

// La réponse de la plateforme, mot pour mot. « 400 Bad Request » ne dit pas ce
// qui cloche ; le corps de la réponse, lui, le dit — et il fallait aller le lire
// dans le journal. Un écran qui affiche le nombre et cache la phrase fait perdre
// exactement l'information qu'on cherche.
function montrer(d) {
  const ok = !!d.reussi;
  $('r-titre').textContent = ok
    ? `Pièce ${d.piece} certifiée`
    : `Pièce ${d.piece || ''} non certifiée`;
  $('r-titre').className = ok ? 'titre-ok' : 'titre-ko';
  $('r-message').textContent = d.message || 'Réponse illisible de l\'agent.';

  if (ok && d.referenceFne) {
    $('r-message').textContent += `\nRéférence FNE : ${d.referenceFne}`;
  }

  const brut = (d.reponsePlateforme || '').trim();
  $('r-brut').hidden = !brut;
  $('r-brut').textContent = brut;
  $('r-brut-libelle').textContent = brut
    ? `Réponse de la plateforme${d.codeHttp ? ' (HTTP ' + d.codeHttp + ')' : ''}, mot pour mot :`
    : (d.codeHttp ? `La plateforme a répondu HTTP ${d.codeHttp} sans corps de réponse.` : '');

  $('resultat').showModal();
}

$('r-ok').onclick = () => $('resultat').close();

chargerModes().then(charger);
setInterval(() => { if (!occupe) charger(); }, 15000);
</script>
</body>
</html>
""";
}
