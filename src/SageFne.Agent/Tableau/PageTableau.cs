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

const PASTILLES = {
  ACertifier:     ['p-pret',    'à certifier'],
  DejaCertifiee:  ['p-fait',    'certifiée'],
  Transmise:      ['p-fait',    'au portail'],
  EnSuspens:      ['p-attente', 'envoi en suspens'],
  ModifieeDepuis: ['p-bloque',  'modifiée après certification'],
  Bloquee:        ['p-bloque',  'bloquée'],
  HorsPerimetre:  ['p-neutre',  'hors périmètre'],
};

async function charger() {
  try {
    const [e, f] = await Promise.all([
      fetch('/api/etat').then((r) => r.json()),
      fetch('/api/factures').then((r) => r.json()),
    ]);
    etat = e;
    dessinerEtat(e);
    dessinerFactures(f);
  } catch (err) {
    $('bandeau').innerHTML = '<span class="jeton alerte">Agent injoignable</span>';
  }
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
  j.push(`<span class="jeton">Fenêtre <b>${e.fenetreJours} j</b></span>`);
  j.push(`<span class="jeton">Lu à ${html(e.lu)}</span>`);
  $('bandeau').innerHTML = j.join('');

  const avis = [];
  if (!e.surDonneesReelles) {
    avis.push("L'agent ne lit pas votre dossier Sage mais un jeu d'essai. "
      + "Rien de ce qui s'affiche ici n'est réel, et rien ne peut être certifié.");
  }
  if (e.environnement === 'PRODUCTION') {
    avis.push('La plateforme est en PRODUCTION : chaque certification est un acte fiscal réel.');
  }
  if (!e.plateformeJoignable) {
    avis.push('La plateforme DGI ne répond pas : ' + html(e.plateformeExplication));
  }
  $('avis').innerHTML = avis.map((a) => `<div class="avis">${a}</div>`).join('');

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

    const action = l.certifiable
      ? `<button data-piece="${html(l.piece)}"${occupe ? ' disabled' : ''}>Certifier</button>`
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

    return `<tr>
      <td>${jour(l.date)}</td>
      <td class="piece">${html(l.piece)}</td>
      <td>${html(l.clientNom || l.client)}
          <div class="motif">${html(l.client)}${l.clientNcc ? ' · NCC ' + html(l.clientNcc) : ''}</div></td>
      <td class="num">${fcfa(l.totalTTC)}</td>
      <td><span class="pastille ${classe}">${html(libelle)}</span>
          ${conduite}
          <div class="motif">${l.certifiable ? 'L\'agent, seul&nbsp;: ' : ''}${html(l.explication)}</div>
          ${codes ? `<div class="codes">${codes}</div>` : ''}</td>
      <td class="num">${action}</td>
    </tr>`;
  }).join('');

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
  $('confirmation').returnValue = '';
  $('confirmation').showModal();
  $('c-oui').onclick = () => { $('confirmation').close(); certifier(piece); };
}

$('c-non').onclick = () => $('confirmation').close();

async function certifier(piece) {
  occupe = piece;
  document.querySelectorAll('button[data-piece]').forEach((b) => (b.disabled = true));
  try {
    const r = await fetch(`/api/factures/${encodeURIComponent(piece)}/certifier`,
                          { method: 'POST' });
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

charger();
setInterval(() => { if (!occupe) charger(); }, 15000);
</script>
</body>
</html>
""";
}
