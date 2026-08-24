# Passerelle FNE → Sage

Transforme les factures certifiées sur la plateforme **FNE** (Facture Normalisée Électronique,
DGI Côte d'Ivoire) en fichier d'import pour **Sage 100 Gestion commerciale**, format paramétrable.

Tout le calcul se fait sur le poste : la page est un fichier HTML autonome, aucun fichier ne part
sur un serveur.

## Ce qui entre

| Source | Contenu | Remarque |
| --- | --- | --- |
| **Factures FNE en texte** | Le PDF des factures, converti en texte ou en Markdown | Source courante : c'est la seule sortie que FNE donne en nombre |
| **Export JSON de FNE** | Le détail certifié, prix unitaires compris | Préférable quand la plateforme le donne : rien n'est déduit |

Le PDF arrondit le prix unitaire au franc — `1 077` pour `1 077,2763`. La passerelle le rétablit
depuis le montant HT certifié et la quantité, si bien que le total recalculé par Sage est celui de
la facture. L'export JSON, lui, porte le prix exact.

## Ce qui sort

Le format relevé sur l'exemplaire que le dossier importe sans difficulté : **quatorze zones
tabulées**, encodage Windows-1252, fins de ligne CRLF, dates `jjmmaa`, virgule décimale.

| | Zone | | | Zone |
| --- | --- | --- | --- | --- |
| 1 | *(vide)* | | 8 | Référence article |
| 2 | Date du document | | 9 | Désignation |
| 3 | Dépôt | | 10 | Prix unitaire (6 décimales) |
| 4 | Type de document | | 11 | Quantité (4 décimales) |
| 5 | Numéro de pièce | | 12 | Unité |
| 6 | Date de livraison | | 13 | Code taxe |
| 7 | Compte tiers | | 14 | Taux de la taxe (4 décimales) |

**Ce format porte la taxe.** Le taux de chaque ligne est écrit — 18, 9, 0, ou 1,5 pour l'AIRSI —
tel que FNE l'a certifié. La fiche article Sage ne décide de rien.

Aucune ligne d'entête, aucune ligne de clôture : un enregistrement par ligne d'article.

## Ce qu'il faut renseigner

Deux tables, gardées sur le poste d'une session à l'autre :

- **Comptes tiers** — `NCC ou nom du client;compte Sage`. FNE nomme le client, Sage l'attend par
  son compte. Les clients rencontrés sans correspondance sont présentés après conversion, avec une
  case pour saisir leur compte.
- **Unités** — `unité de la facture;unité Sage`, par exemple `CARTONS;CN`.

Les **références d'article n'ont pas de table** : FNE est alimenté depuis le catalogue du dossier,
les deux nomenclatures coïncident déjà.

## Développement

```bash
npm install
npm test        # moteur : lecture, écriture, conversion
npm run build   # assemble web/dist/passerelle-fne-sage.html
npm run verify  # la page, dans un vrai navigateur
```

Le contrôle qui compte est l'**aller-retour** (`tests/ecriture-sage.test.ts`) : un exemplaire du
format que Sage accepte est relu, ses documents reconstruits, puis réécrits — le fichier produit
doit ressortir octet pour octet.

## Structure

| Fichier | Rôle |
| --- | --- |
| `src/modele.ts` | Ce qu'une facture doit porter, et rien de plus |
| `src/lire-factures.ts` | Le texte des factures certifiées → factures |
| `src/lire-json.ts` | L'export JSON de FNE → factures |
| `src/comptes.ts` | Clients et unités : rapprochement avec le dossier |
| `src/controles.ts` | Ce qui doit être vu avant l'import |
| `src/ecrire-sage.ts` | Le fichier à quatorze zones |
| `src/convertir.ts` | L'enchaînement, sans état |
| `web/` | La page, et le script qui l'assemble |
