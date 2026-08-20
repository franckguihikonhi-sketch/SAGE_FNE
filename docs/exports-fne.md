# Les exports FNE

La plateforme FNE (Facture Normalisée Électronique, DGI Côte d'Ivoire) propose deux exports.
**Ils ne contiennent pas la même chose**, et ce point conditionne tout le reste.

| Export | Contenu | Utilisable pour un import Sage |
| --- | --- | --- |
| **JSON** | Entêtes **et** détail des articles (`items`), avec le taux de TVA de chaque ligne | Oui, sans réserve |
| **Tableur (.xlsx)** | Entêtes uniquement : 33 colonnes, une ligne par facture, aucun article | Partiellement (voir plus bas) |

## Export JSON — format natif

Tableau d'objets facture. Le connecteur le reconnaît automatiquement et le lit sans passer par
la détection de colonnes.

| Champ FNE | Modèle pivot | Remarque |
| --- | --- | --- |
| `reference` | numéro de pièce / référence FNE | ex. `2304903U26000000889` |
| `parentReference` | facture d'origine d'un avoir | |
| `token` | code de vérification | jeton de l'URL de vérification |
| `subtype` | nature du document | `normal` → facture, `refund` → avoir |
| `date` | date du document | ISO avec heure |
| `paymentMethod` | mode de règlement | `cash`, `card`, `check`, `mobile-money`, `transfer`, `deferred` |
| `template` | type de facturation | `B2B`, `B2C`, `B2G`, `B2F` |
| `clientNcc`, `clientCompanyName`, `clientPhone`, `clientEmail` | client | |
| `clientSellerName`, `clientEstablishment`, `clientPointOfSale` | traçabilité | |
| `totalBeforeTaxes`, `totalTaxes`, `totalAfterTaxes`, `totalDue` | totaux | |
| `fiscalStamp`, `totalDiscounted`, `totalCustomTaxes` | timbre, remise, autres taxes | |
| `items[].quantity` | quantité | |
| `items[].amount` | **prix unitaire HT** | et non le montant de la ligne |
| `items[].reference`, `items[].description` | référence et désignation de l'article | |
| `items[].measurementUnit` | unité | ex. `SAC`, `CARTON` |
| `items[].discount` | remise sur l'article | **interprétée comme un pourcentage** — à confirmer, aucun exemple non nul dans les exports fournis |
| `items[].taxes[0].amount` | taux de TVA | valeur en pourcentage (18, 9, 0) |
| `items[].taxes[0].shortName` | code taxe | `TVA`, `TVAB`, `TVAC`, `TVAD` |

Les avoirs (`subtype: "refund"`) portent des montants **négatifs**. Sage exprimant l'avoir par le
type de document, le connecteur rétablit des montants positifs (option `avoirEnValeurAbsolue`,
activée par défaut).

Une ligne ne portant qu'une seule taxe est reprise telle quelle ; au-delà, seule la première est
transmise à Sage et une anomalie est signalée.

## Export tableur — entêtes seuls

Colonnes de l'export : Référence initial, Token, Référence, Type de facture, Sous-type de facture,
Date, Mode de paiement, Timbre de quittance, Total HT, Remise, Total TVA, Total TTC, Total Autres
taxes, Net a payer, NCC du client, Nom de la société / du client, Téléphone du client, Email du
client, Terminal, RCCM, Nom du vendeur, Établissement, Point de vente, Régime d'imposition, Type de
facturation, Autres Mentions, Pied de page, Devises étrangères, Taux de change, Est RNE, RNE,
Créé à, Mise à jour à.

Sans article, le connecteur reconstitue **une ligne de synthèse par facture** à partir des totaux :
quantité 1, prix unitaire = total HT, taux = total TVA ÷ total HT.

**Cette reconstitution n'est valable que si la facture ne porte qu'un seul taux de TVA.** Sur
l'export de contrôle fourni (50 factures), 14 factures donnent un taux reconstitué hors nomenclature
FNE (13,77 %, 15,61 %, 16,54 %…) : ce sont des factures mélangeant 18 % et 0 % ou 9 %. Le connecteur
les bloque avec le code `TAUX_TVA_NON_CONFORME` plutôt que de produire une TVA fausse dans Sage.

**En pratique : utiliser l'export JSON.** L'export tableur convient pour un rapprochement ou pour
des lots mono-taux, pas pour un import comptable complet.

## Codes de la nomenclature FNE

Source : *Procédure de certification des factures des entreprises par API*, DGI, mai 2025, annexe 1.

| `taxes` | Libellé | Taux |
| --- | --- | --- |
| `TVA` | TVA normal | 18 % |
| `TVAB` | TVA réduit | 9 % |
| `TVAC` | TVA exonération conventionnelle | 0 % |
| `TVAD` | TVA exonération légale (TEE et RME) | 0 % |

| `paymentMethod` | | `template` | |
| --- | --- | --- | --- |
| `cash` | espèce | `B2B` | client professionnel possédant un NCC |
| `card` | carte bancaire | `B2C` | client particulier |
| `check` | chèque | `B2G` | institution gouvernementale |
| `mobile-money` | mobile money | `B2F` | client à l'étranger |
| `transfer` | virement bancaire | | |
| `deferred` | à terme | | |

`invoiceType` vaut `sale` (vente) ou `purchase` (achat). Devises acceptées : XOF, USD, EUR, JPY,
CAD, GBP, AUD, CNH, CHF, HKD, NZD.

## Structure d'une référence FNE

`2304903U26000000889` = NCC de l'émetteur (`2304903U`) + année (`26`) + numéro d'ordre
(`000000889`). Un avoir est préfixé par `A` : `A2304903U2600000038`.

La référence complète fait 19 caractères, au-delà du champ numéro de pièce de Sage (13). Par défaut
le connecteur transmet la partie année + numéro (`26000000889`), le NCC étant constant pour une
entreprise ; la référence complète reste écrite dans une zone dédiée du fichier d'import.
