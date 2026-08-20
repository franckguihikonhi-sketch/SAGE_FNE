# Reconnaissance des colonnes de l'export FNE

## Fonctionnement

À la lecture du fichier, chaque libellé de colonne est normalisé (accents, casse et ponctuation
retirés, `%` conservé sous forme du mot `pct`) puis comparé au dictionnaire d'alias de
`src/lib/fne/fields.ts`. La correspondance exacte est testée avant la correspondance partielle,
et les alias les plus longs sont testés en premier pour éviter qu'une colonne « TVA » ne capture
« Montant TVA ligne ».

Une colonne non reconnue n'est jamais devinée : elle apparaît dans `unmappedColumns` et peut être
associée manuellement à un champ via `mappingOverrides`.

## Champs du modèle pivot

**Entête de facture** : numéro de facture, numéro FNE, code de vérification, date, type de
document, code / nom / NCC / adresse / téléphone / email du client, devise, référence, mode de
règlement, totaux (HT, remise, TVA, autres taxes, TTC).

**Lignes** : numéro de ligne, référence article, désignation, quantité, unité, prix unitaire HT,
remise (%), code taxe, taux de TVA, montants HT / TVA / TTC.

## Regroupement des lignes

Un export FNE fournit en général une ligne par article, les zones d'entête étant répétées. Les
lignes sont regroupées par **numéro de facture** ; les zones d'entête sont lues sur la première
ligne de chaque groupe.

## Valeurs déduites

Quand une donnée manque, elle est recalculée plutôt que laissée vide :

| Donnée absente | Calcul |
| --- | --- |
| Montant HT de la ligne | `quantité × prix unitaire × (1 − remise%)`, sinon `TTC − TVA` |
| Prix unitaire | `montant HT ÷ (quantité × (1 − remise%))` |
| Taux de TVA | code taxe FNE, sinon `TVA ÷ HT`, sinon taux par défaut (18 %) |
| Montant TVA | `HT × taux` |
| Montant TTC | `HT + TVA` |
| Totaux facture | somme des lignes |

Les écarts entre totaux déclarés et totaux recalculés sont signalés (`ECART_TOTAL_HT`,
`ECART_TOTAL_TVA`) sans bloquer la conversion.

## Codes taxe

`src/lib/fne/taxes.ts` fait la correspondance code taxe FNE → taux. Valeurs posées par défaut,
**à confirmer sur un export réel** :

| Code | Libellé | Taux |
| --- | --- | --- |
| `TVA` | TVA taux normal | 18 % |
| `TVAB` | TVA taux réduit | 9 % |
| `TVAC` | Exonération conventionnelle | 0 % |
| `TVAD` | Exonération légale | 0 % |
| `EXO` | Exonéré | 0 % |

Un code inconnu accompagné d'aucun taux exploitable remonte une anomalie.

## Factures d'avoir

Une facture est traitée comme un avoir si la colonne « type de document » contient
*avoir*, *refund*, *credit*, *annulation* ou *remboursement*, ou, à défaut de colonne de type,
si toutes ses lignes portent des montants négatifs.

## Formats de fichier acceptés

| Extension | Traitement |
| --- | --- |
| `.csv`, `.txt` | Séparateur détecté automatiquement ; UTF-8 (avec ou sans BOM) ou Windows-1252 |
| `.xlsx`, `.xlsm` | Première feuille contenant des données ; ligne d'entête détectée dans les 20 premières lignes |
| `.json` | Tableau d'objets ou objet contenant `data` / `items` / `invoices` / `factures` ; objets imbriqués aplatis (`client.ncc`) |
| `.xls` | Non supporté — enregistrer en `.xlsx` ou `.csv` |
