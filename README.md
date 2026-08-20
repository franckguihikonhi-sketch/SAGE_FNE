# SAGE FNE

Convertisseur des exports de factures **FNE** (Facture Normalisée Électronique, DGI Côte d'Ivoire)
vers un fichier d'import **Sage 100 Gestion Commerciale** (Fichier > Importer > *Format
paramétrable*), afin de retrouver automatiquement les factures dans les documents des ventes.

```
Export FNE (CSV / Excel / JSON)
        │
        ├─ lecture + détection automatique des colonnes
        ├─ modèle pivot (facture, client, lignes, taxes)
        ├─ correspondance clients FNE → comptes tiers Sage
        ├─ contrôles avant import (totaux, doublons, longueurs de zones)
        │
        └─→ fichier d'import Sage 100 (texte tabulé, Windows-1252, CRLF)
```

## État du projet

Le moteur de conversion, les contrôles et l'interface web sont fonctionnels et testés sur un
export d'exemple. **Deux points restent à caler sur les fichiers réels** — ils sont isolés dans
des fichiers de configuration, pas dans le code :

1. **Les colonnes de l'export FNE** — `src/lib/fne/fields.ts` contient un dictionnaire d'alias
   couvrant les libellés attendus. Toute colonne non reconnue est signalée à l'utilisateur, jamais
   ignorée silencieusement. Il suffit d'ajouter les libellés réels à la liste.
2. **Le format d'import Sage** — `src/lib/sage/profile.ts` décrit l'ordre des zones, les codes
   `DO_Type` et les séparateurs. Il doit être aligné sur le format d'import (`.imp`) défini dans
   le dossier Sage du client. Voir `docs/format-import-sage.md`.

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
npm run convert -- export-fne.xlsx \
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
| `PIECE_TROP_LONGUE` | avertissement | Numéro de pièce au-delà de la longueur Sage |
| `DESIGNATION_TRONQUEE` | avertissement | Désignation au-delà de la longueur Sage |
| `ECART_TOTAL_HT` / `ECART_TOTAL_TVA` | avertissement | Totaux déclarés ≠ totaux recalculés depuis les lignes |
| `QUANTITE_NULLE` | avertissement | Ligne à quantité nulle |

Le téléchargement du fichier est bloqué tant qu'il reste une erreur bloquante.

## Organisation du code

```
src/lib/core/      modèle pivot + parseurs de montants et de dates (formats FR, séries Excel)
src/lib/fne/       lecture des exports FNE, dictionnaire de colonnes, codes taxe, normalisation
src/lib/sage/      profils d'import, jetons, écriture du fichier, correspondance clients
src/lib/report/    contrôles avant import
src/lib/pipeline.ts  chaîne complète : fichier FNE → fichier Sage
src/app/           interface web (Next.js App Router) et API /api/convert
scripts/convert.ts CLI
docs/              documentation fonctionnelle
```

La conversion se fait **entièrement en mémoire** : aucun fichier n'est stocké côté serveur.

## Prochaines étapes

- Caler le dictionnaire de colonnes sur un export FNE réel.
- Reproduire à l'identique le format d'import (`.imp`) du dossier Sage cible.
- Interface de mappage manuel des colonnes non reconnues (l'API l'accepte déjà via
  `mappingOverrides`).
- Persistance de la table de correspondance clients, puis multi-société / multi-utilisateur.
