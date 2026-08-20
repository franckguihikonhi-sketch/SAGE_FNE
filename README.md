# SAGE FNE

Convertisseur des exports de factures **FNE** (Facture Normalisée Électronique, DGI Côte d'Ivoire)
vers un fichier d'import **Sage 100 Gestion Commerciale** (Fichier > Importer > *Format
paramétrable*), afin de retrouver automatiquement les factures dans les documents des ventes.

```
Export FNE (JSON natif / Excel / CSV)
        │
        ├─ lecture : JSON natif lu directement, tableaux par détection de colonnes
        ├─ modèle pivot (facture, client, lignes, taxes)
        ├─ correspondance clients FNE → comptes tiers Sage
        ├─ contrôles avant import (totaux, doublons, longueurs de zones)
        │
        └─→ fichier d'import Sage 100 (texte tabulé, Windows-1252, CRLF)
```

## État du projet

Le côté FNE est **calé sur des exports réels** (un export JSON et un export tableur de 50 factures)
et sur la documentation officielle *Procédure de certification des factures des entreprises par API*
(DGI, mai 2025) : codes taxe, modes de paiement, types de facturation et structure des références
sont conformes à la nomenclature publiée. Voir `docs/exports-fne.md`.

Le côté Sage est calé lui aussi : le profil par défaut `sage100-import-export` **reproduit le format
paramétrable du dossier client** (fichier `FORMAT IMPORT_EXPORT.egc` et fichier d'exemple réellement
échangé avec Sage) — 15 zones tabulées, format à plat, dates `jjmmaa`, séparateur décimal virgule,
encodage Windows-1252. Voir `docs/format-import-sage.md`.

**Le point d'attention restant est la TVA.** Ce format ne comporte aucune zone de taxe : Sage
applique le régime de TVA de la fiche article, pas le code taxe FNE. Le connecteur le signale, en
erreur bloquante dès qu'une facture sort du taux normal.

### Quel export FNE utiliser

FNE propose trois exports. Le **PDF** est la facture certifiée, à lire et à archiver : ses montants
sont arrondis au franc, il est refusé à l'import avec un message qui l'explique.

L'export **JSON** est le seul à contenir le détail des articles. L'export **tableur** ne porte que
les entêtes de facture : le connecteur reconstitue alors une ligne de synthèse par facture, ce qui
n'est exact que pour les factures à un seul taux de TVA. Sur l'export de contrôle fourni, 14 des
50 factures mélangent plusieurs taux et sont bloquées plutôt que converties avec une TVA fausse.

## Démarrage

```bash
npm install
npm run dev        # interface web sur http://localhost:3000
npm test           # tests unitaires
npm run typecheck
npm run build
```

### Conversion en ligne de commande

```bash
npm run convert -- factures_20260811.json \
  --profil=sage100-documents-ventes \
  --clients=clients.csv \
  --defaut=411DIVERS \
  --sortie=import-sage.txt
```

`clients.csv` associe les clients FNE aux comptes tiers Sage, une ligne par client :

```
ncc;nom;compte Sage
1234567 A;ETS KOUAME ET FILS;411KOUAME
7654321 B;;411SID
```

Le NCC seul ou le nom seul suffit ; la colonne `compte Sage` est obligatoire.

## Formats d'import disponibles

| Identifiant | Description |
| --- | --- |
| `sage100-documents-ventes` | Texte tabulé, un enregistrement d'entête `E` par facture suivi de ses lignes `L`. |
| `sage100-ligne-a-plat` | Texte tabulé, une ligne par article avec les zones d'entête répétées. |
| `sage100-csv-controle` | CSV point-virgule avec libellés, pour relire le résultat dans Excel avant l'import. |

Ajouter un format revient à déclarer un profil dans `src/lib/sage/profile.ts` : aucun code à écrire,
les zones sont décrites par des jetons (`document.numero`, `client.code`, `ligne.montantHT`, …).
La liste complète des jetons est dans `src/lib/sage/tokens.ts`.

## Contrôles effectués avant l'import

| Code | Gravité | Contrôle |
| --- | --- | --- |
| `PIECE_DUPLIQUEE` | erreur | Numéro de pièce présent plusieurs fois |
| `DATE_MANQUANTE` | erreur | Date absente ou illisible |
| `COMPTE_TIERS_MANQUANT` | erreur | Client sans compte tiers Sage |
| `FACTURE_SANS_LIGNE` | erreur | Facture sans ligne d'article |
| `TAXE_ABSENTE_DU_FORMAT` | erreur / avertissement | Le format d'import ne transporte pas la TVA ; Sage appliquera celle de l'article |
| `TAUX_TVA_NON_CONFORME` | erreur | Taux hors nomenclature FNE (18 / 9 / 0 %), typiquement une facture à plusieurs taux reconstituée depuis un export sans articles |
| `PIECE_TROP_LONGUE` | avertissement | Numéro de pièce au-delà de la longueur Sage |
| `DESIGNATION_TRONQUEE` | avertissement | Désignation au-delà de la longueur Sage |
| `ECART_TOTAL_HT` / `ECART_TOTAL_TVA` | avertissement | Totaux déclarés ≠ totaux recalculés depuis les lignes |
| `QUANTITE_NULLE` | avertissement | Ligne à quantité nulle |

Le téléchargement du fichier est bloqué tant qu'il reste une erreur bloquante.

## Organisation du code

```
src/lib/core/      modèle pivot + parseurs de montants et de dates (formats FR, séries Excel)
src/lib/fne/       lecture des exports FNE (JSON natif + tableaux), nomenclature DGI, normalisation
src/lib/sage/      profils d'import, jetons, écriture du fichier, correspondance clients
src/lib/report/    contrôles avant import
src/lib/pipeline.ts  chaîne complète : fichier FNE → fichier Sage
src/app/           interface web (Next.js App Router) et API /api/convert
scripts/convert.ts CLI
supabase/          migrations SQL, stub d'authentification et tests d'isolation
docs/              documentation fonctionnelle
```

`docs/exports-fne.md` décrit les deux exports FNE et la nomenclature DGI ;
`docs/format-import-sage.md` explique comment aligner le fichier généré sur le paramétrage Sage ;
`docs/mapping-fne.md` détaille la reconnaissance des colonnes des exports tableur.

La conversion se fait **entièrement en mémoire** : aucun fichier n'est stocké côté serveur.

## Version web autonome

`npm run build:web` compile le moteur pour le navigateur et produit une page unique,
`web/dist/passerelle-fne-sage.html`, qui embarque tout le convertisseur : dépôt du fichier,
contrôles, synthèse par article et génération du fichier Sage se font entièrement côté client,
aucun fichier n'est transmis.

Le poste garde ses réglages d'une session à l'autre (`localStorage`) : format d'import, dépôt,
souche, mode de numérotation, compte tiers par défaut, table de correspondance clients et modes de
règlement. Les clients sans compte tiers ne sont pas listés comme des erreurs mais présentés comme
un travail à faire — un champ par client, mémorisé pour les conversions suivantes. La table clients
s'importe et s'exporte en CSV.

`npm run verify:web` rejoue dans Chromium l'ensemble du parcours : conversion des deux formes
d'export, refus du PDF, affectation des comptes tiers et persistance des réglages après
rechargement.

Cette page ne dépend d'aucun module Node : le lecteur Excel (ZIP + XML via `DecompressionStream`)
et l'encodeur Windows-1252 sont écrits dans `src/lib/browser/` et `src/lib/core/cp1252.ts`. Le
pipeline reçoit son lecteur en paramètre, ce qui lui permet de tourner à l'identique des deux côtés.

## Base de données

Les migrations Supabase sont dans `supabase/migrations/` : dossiers multi-société, correspondance
clients partagée, historique des conversions et **détection des doublons** (une référence FNE ne
peut entrer qu'une fois par dossier). Le détail des lignes d'articles n'est jamais stocké — seul
l'entête des factures remonte. `npm run test:db` applique les migrations sur un PostgreSQL local et
rejoue quinze contrôles d'isolation. Voir `docs/base-de-donnees.md`.

La base n'est pas encore branchée à l'application : elle attend l'URL du projet et la clé `anon`.

## Confidentialité

Les exports FNE contiennent des données clients et, pour l'export JSON, la **clé API** de
l'entreprise (`company.apiKey`). Le dossier `samples/` est exclu du dépôt par `.gitignore` : ne
jamais y committer d'export réel. Les jeux de test de `tests/fixtures/` sont anonymisés.

## Prochaines étapes

- Faire confirmer par le client les zones 1, 6 et 14 du format (constantes reprises de l'échantillon).
- Ajouter une zone de taxe au format d'import Sage pour porter le code taxe FNE.
- Confirmer la sémantique du champ `discount` de FNE (pourcentage ou montant).
- Interface de mappage manuel des colonnes non reconnues (l'API l'accepte déjà via
  `mappingOverrides`).
- Persistance des tables de correspondance (clients, règlements), puis multi-société /
  multi-utilisateur.
- Packaging en application Windows installable (.exe), une fois le convertisseur validé en ligne.
- Lecture directe depuis l'API FNE (`/external/invoices`) pour supprimer l'étape d'export manuel.
