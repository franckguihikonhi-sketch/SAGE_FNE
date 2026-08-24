# Format d'import Sage 100 Gestion Commerciale

## Le format du dossier client (profil par défaut)

Le profil `sage100-export-verifie` reproduit **l'exemplaire que le dossier importe sans
difficulté** (`EXPORT_SAGE_VERIFIE.txt`, 60 enregistrements), relevé zone par zone.

| Caractéristique | Valeur |
| --- | --- |
| Séparateur | tabulation |
| Fin de ligne | CRLF |
| Encodage | Windows-1252 |
| Séparateur décimal | virgule |
| Format de date | `jjmmaa` (`200826` = 20/08/2026) |
| Structure | **à plat** : une ligne par article, zones d'entête répétées |
| Nombre de zones | 14 |

### Les 14 zones

| # | Zone | Source | Exemple |
| --- | --- | --- | --- |
| 1 | *(vide)* | vide sur les 60 lignes de l'exemplaire | |
| 2 | Date du document | date FNE en `jjmmaa` | `110826` |
| 3 | Dépôt | paramètre `depot` | `DEPÔT PRINCIPAL SOGEL` |
| 4 | Type de document | `6` facture, `5` avoir | `6` |
| 5 | Numéro de pièce | numéro FNE, ou vide | `522` |
| 6 | Date de livraison | date FNE, réglable | `110826` |
| 7 | Compte tiers | table de correspondance clients | `4111COCODYPALISAD` |
| 8 | Référence article | `items[].reference` | `6FF001` |
| 9 | Désignation | `items[].description` | `FRITES 7 MM-PK` |
| 10 | Prix unitaire HT | `items[].amount`, 6 décimales | `1077,276300` |
| 11 | Quantité | `items[].quantity`, 4 décimales | `20,0000` |
| 12 | Unité | `items[].measurementUnit` | `SAC` |
| 13 | Code taxe | `TVA` si le taux est non nul, sinon vide ; un code hors nomenclature FNE est repris tel quel | `TVA`, `AIRSI` |
| 14 | Taux de TVA | taux de la ligne, 4 décimales | `18,0000` |

**Ce format transporte la taxe.** Le taux est écrit ligne à ligne — 18, 9 ou 0 — et le code taxe
l'accompagne. Le régime de la fiche article Sage ne décide donc plus de rien : ni rappel sur la TVA,
ni blocage sur un article vu à deux taux, ni besoin d'un article distinct par taux pour les
factures reconstituées.

Sur l'exemplaire, le code `AIRSI` apparaît aussi, à 1,5 % — un prélèvement ivoirien. FNE ne le
certifie pas comme la TVA d'une ligne : il apparaît dans `customTaxes`, et le connecteur le
signale plutôt que de l'inventer. Une ligne qui porterait déjà ce code le conserve.

## Pourquoi le format d'export du dossier n'est pas importable

Le profil `sage100-import-export`, calqué sur le fichier `.egc` **FORMAT IMPORT_EXPORT** et sur un
fichier d'export du dossier, porte **15 zones**. Sage le refuse à l'import, y compris quand c'est
son propre export qu'on lui redonne, avec le message *« Le champ Type document est incorrect à la
ligne 1 »*.

Les deux formats sont identiques à deux différences près, et ce sont elles qui expliquent le refus :

| | Export (15 zones) | Exemplaire importé (14 zones) |
| --- | --- | --- |
| Zone 1 | constante `0` | *(vide)*, et pas de zone en plus |
| Zones 14-15 | *(vide)* puis `0,0000` | code taxe puis taux de TVA |

La constante de tête décale tout d'un cran : Sage lit le **dépôt** là où il attend le **type de
document**, d'où le message. Et la dernière zone n'est pas une remise mais le taux de TVA — dans
l'export elle valait `0,0000` parce que les articles concernés étaient exonérés.

Le profil à 15 zones reste disponible dans la liste des formats, à titre de témoin.

### Les références d'article ne coïncident pas

L'exemplaire réel du dossier porte des références d'article **numériques** — `1147005`, `1149001`,
`1149002` — là où FNE certifie des références alphanumériques comme `6FF001`. Les deux nomenclatures
sont indépendantes : transmettre la référence FNE telle quelle ferait rejeter la ligne par Sage, ou
créerait un article inconnu.

Le connecteur porte donc une **table de correspondance articles** (`référence FNE ; référence Sage`),
sur le modèle de celle des comptes tiers : les articles sans correspondance sont listés à l'écran
avec un champ de saisie, et la table est conservée d'une conversion à l'autre. Sans correspondance,
la référence FNE est transmise telle quelle et l'article est signalé.

### Pas de ligne de clôture

Un premier fichier de référence se terminait par un enregistrement sans article — type, numéro,
date de livraison et compte tiers, le reste à vide — et le connecteur en écrivait un par document.

L'exemplaire de 60 enregistrements réellement importé l'a démenti : **57 documents, une seule
ligne sans article** (le document 536). Ce n'est donc pas une règle du format mais une ligne sans
article dans le dossier. Le connecteur n'en écrit plus.

### Preuve par l'aller-retour

L'exemplaire est relu, ses documents reconstruits, puis réécrits par le connecteur : **59 des 60
enregistrements ressortent octet pour octet** — même contenu, même ordre, même encodage, 7 744
octets contre 7 744. Le soixantième est la ligne sans article ci-dessus.

Le contrôle est rejoué à chaque exécution des tests sur un exemplaire anonymisé de même structure
(`tests/fixtures/sage-export-verifie.txt`) : prélèvement AIRSI à 1,5 %, ligne exonérée, taux
réduit, taux normal, document à deux lignes, type 8 à numérotation distincte, et une pièce dont la
date de livraison diffère de la date du document.

### Ce que ce format ne transporte pas

Le format à 14 zones **porte la taxe** : le taux de chaque ligne est écrit, et le contrôle
`TAXE_ABSENTE_DU_FORMAT` ne se déclenche donc pas. Il reste actif pour les profils qui n'ont aucune
zone de taxe, dont l'ancien profil à 15 zones : Sage applique alors le régime de la fiche article,
ce qui fausse les exonérations et le taux réduit.

Ce format ne transporte pas les totaux (HT, TVA, TTC) : Sage les recalcule depuis prix
unitaire × quantité. Les totaux FNE servent alors uniquement aux contrôles du connecteur.

## Numéro de pièce et référence FNE

Une référence FNE fait 19 caractères (`2304903U26000000889`), au-delà de la zone `DO_Piece` de
Sage. Par défaut seule la partie année + numéro d'ordre est transmise comme numéro de pièce
(`26000000889`, 11 caractères) : le NCC en préfixe est constant pour une entreprise. Ce format ne comporte
aucune zone où loger la référence complète : pour la traçabilité, mieux vaut ajouter une zone
dédiée au format d'import dans Sage.

Basculer sur `numeroPiece: "reference"` transmet la référence complète — à réserver aux dossiers
dont la zone a été étendue, sinon Sage tronquera. Le mode `vide`, qui est le défaut, laisse la zone vide
pour que Sage numérote lui-même ; l'unicité est alors contrôlée sur la référence FNE. À noter que
l'exemplaire importé, lui, porte toujours un numéro (`443`, `522`, `FR00035`) : si Sage réclame
cette zone, basculer sur `sequence`.

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
