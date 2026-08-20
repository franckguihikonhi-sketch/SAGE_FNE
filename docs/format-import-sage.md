# Format d'import Sage 100 Gestion Commerciale

## Le format du dossier client (profil par défaut)

Le profil `sage100-import-export` reproduit le format paramétrable
**FORMAT IMPORT_EXPORT** du dossier client, relevé sur son fichier `.egc` et sur un fichier
d'exemple réellement échangé avec Sage.

| Caractéristique | Valeur |
| --- | --- |
| Séparateur | tabulation |
| Fin de ligne | CRLF |
| Encodage | Windows-1252 |
| Séparateur décimal | virgule |
| Format de date | `jjmmaa` (`200826` = 20/08/2026) |
| Structure | **à plat** : une ligne par article, zones d'entête répétées |
| Nombre de zones | 15 |

Le fichier `.egc` déclare 19 zones dont 15 retenues, dans l'ordre `0-6`, `11-16`, `20`, `21` —
exactement le nombre de colonnes du fichier d'exemple. Les zones 7, 8, 17 et 18 ne sont pas reprises.

### Les 15 zones

| # | Zone | Source | Exemple |
| --- | --- | --- | --- |
| 1 | Domaine | constante `0` (vente) | `0` |
| 2 | Numéro de pièce | numéro FNE, ou vide | `26000000889` |
| 3 | Date du document | date FNE en `jjmmaa` | `110826` |
| 4 | Dépôt | paramètre `depot` | `DEPÔT PRINCIPAL SOGEL` |
| 5 | Type de document | `6` facture, `5` avoir | `6` |
| 6 | Souche | paramètre `souche` | `1` |
| 7 | Date de livraison | date FNE | `110826` |
| 8 | Compte tiers | table de correspondance clients | `411PROSUMA` |
| 9 | Référence article | `items[].reference` | `6FF001` |
| 10 | Désignation | `items[].description` | `FRITES 7MM-PK` |
| 11 | Prix unitaire HT | `items[].amount`, 6 décimales | `1077,276300` |
| 12 | Quantité | `items[].quantity`, 4 décimales | `20,0000` |
| 13 | Unité | `items[].measurementUnit` | `SAC` |
| 14 | *(non identifiée)* | vide dans le fichier d'exemple | |
| 15 | Remise | remise de ligne, 4 décimales | `0,0000` |

Trois zones restent à confirmer auprès du client : la 1 (constante `0`, interprétée comme le
domaine Vente), la 6 (constante `1`, interprétée comme la souche) et la 14, vide dans l'exemple.
Elles sont écrites à l'identique de l'échantillon, donc sans risque, mais leur libellé exact
mériterait d'être vérifié dans l'écran de paramétrage du format.

### Ce que ce format ne transporte pas

**Aucune zone de taxe.** Sage appliquera le régime de TVA paramétré sur chaque article, et non le
code taxe porté par la facture FNE (`TVA` 18 %, `TVAB` 9 %, `TVAC`/`TVAD` 0 %). Sans effet tant que
tout est au taux normal, cela **fausse les exonérations et le taux réduit**.

Le connecteur le signale (`TAXE_ABSENTE_DU_FORMAT`) : simple avertissement si toutes les lignes sont
à 18 %, erreur bloquante dès qu'un autre taux apparaît. Deux issues : ajouter une zone de taxe au
format d'import dans Sage (recommandé), ou s'assurer que les articles exonérés sont paramétrés comme
tels dans la fiche article.

Ce format ne transporte pas non plus les totaux (HT, TVA, TTC) : Sage les recalcule depuis prix
unitaire × quantité. Les totaux FNE servent alors uniquement aux contrôles du connecteur.

## Numéro de pièce et référence FNE

Une référence FNE fait 19 caractères (`2304903U26000000889`), au-delà de la zone `DO_Piece` de
Sage. Par défaut seule la partie année + numéro d'ordre est transmise comme numéro de pièce
(`26000000889`, 11 caractères) : le NCC en préfixe est constant pour une entreprise. La référence
complète est toujours écrite dans la zone « Référence FNE » du fichier, pour la traçabilité et les
contrôles de la DGI.

Basculer sur `numeroPiece: "reference"` transmet la référence complète — à réserver aux dossiers
dont la zone a été étendue, sinon Sage tronquera. Le mode `vide` laisse la zone vide pour que Sage
numérote lui-même, comme dans le fichier d'exemple du client ; l'unicité est alors contrôlée sur la
référence FNE.

## Modes de règlement

FNE expose un code (`cash`, `card`, `check`, `mobile-money`, `transfer`, `deferred`) ; Sage attend
un code de règlement propre au dossier. La correspondance se saisit dans l'interface ou via
`--profil` en ligne de commande, sous la forme `deferred=CRED`. Sans correspondance, la zone est
laissée vide plutôt que remplie d'une valeur inventée.

## Longueurs de zones

`src/lib/sage/limits.ts` porte les longueurs standard des champs Sage 100 utilisées par les
contrôles (numéro de pièce, compte tiers, référence article, désignation). Les dépassements
sont signalés avant l'import plutôt que rejetés par Sage.

## Conseils d'import

- Toujours travailler d'abord sur un **dossier de test**.
- Importer un lot réduit (1 à 2 factures) avant le lot complet.
- Vérifier après import : le total TTC du document, le compte tiers, la souche et le taux de TVA
  de chaque ligne.
- Le profil `sage100-csv-controle` produit le même contenu en CSV lisible dans Excel : utile pour
  comparer ligne à ligne avant l'import réel.
