# Reconnaissance des colonnes des exports tableur

> Cette page concerne les exports **tableur** (Excel / CSV). L'export **JSON natif** de FNE est lu
> directement par `src/lib/fne/native.ts`, sans détection de colonnes : voir `docs/exports-fne.md`.

## Fonctionnement

À la lecture du fichier, chaque libellé de colonne est normalisé (accents, casse et ponctuation
retirés, `%` conservé sous forme du mot `pct`) puis comparé au dictionnaire d'alias de
`src/lib/fne/fields.ts`. La correspondance exacte est testée avant la correspondance partielle,
et les alias les plus longs sont testés en premier pour éviter qu'une colonne « TVA » ne capture
« Montant TVA ligne ».

Une colonne non reconnue n'est jamais devinée : elle apparaît dans `unmappedColumns` et peut être
associée manuellement à un champ via `mappingOverrides`. Les colonnes présentes dans l'export FNE
mais sans usage côté Sage (RCCM, Terminal, Pied de page, Créé à…) sont listées séparément dans
`ignoredColumns` : elles ne sont pas des anomalies.

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

## Mode synthèse

L'export tableur FNE ne contient aucune ligne d'article. Quand aucune colonne de détail n'est
reconnue, une ligne unique est reconstituée par facture depuis les totaux, et le taux de TVA est
déduit (`total TVA ÷ total HT`). Un taux déduit hors nomenclature FNE fait échouer le contrôle
`TAUX_TVA_NON_CONFORME` : la facture mélange plusieurs taux et seul l'export JSON permet de la
reprendre correctement.

## Codes taxe

`src/lib/fne/taxes.ts` fait la correspondance code taxe FNE → taux, conformément à l'annexe 1 de la
documentation DGI : `TVA` 18 %, `TVAB` 9 %, `TVAC` 0 %, `TVAD` 0 %. Un code inconnu accompagné
d'aucun taux exploitable remonte une anomalie.

## Factures d'avoir

Une facture est traitée comme un avoir si la colonne « Sous-type de facture » vaut `refund` (ou
contient *avoir*, *credit*, *annulation*, *remboursement*), si une référence de facture d'origine
est renseignée, ou, à défaut, si ses montants sont négatifs.

Les montants d'un avoir sont ramenés en valeurs positives, le type de document Sage portant déjà le
sens de l'opération (option `avoirEnValeurAbsolue`).

## Formats de fichier acceptés

| Extension | Traitement |
| --- | --- |
| `.csv`, `.txt` | Séparateur détecté automatiquement ; UTF-8 (avec ou sans BOM) ou Windows-1252 |
| `.xlsx`, `.xlsm` | Première feuille contenant des données ; ligne d'entête détectée dans les 20 premières lignes |
| `.json` | Tableau d'objets ou objet contenant `data` / `items` / `invoices` / `factures` ; objets imbriqués aplatis (`client.ncc`) |
| `.xls` | Non supporté — enregistrer en `.xlsx` ou `.csv` |
