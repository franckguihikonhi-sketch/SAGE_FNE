# Les exports FNE

La plateforme FNE (Facture Normalisée Électronique, DGI Côte d'Ivoire) propose deux exports.
**Ils ne contiennent pas la même chose**, et ce point conditionne tout le reste.

| Export | Contenu | Utilisable pour un import Sage |
| --- | --- | --- |
| **JSON** | Entêtes **et** détail des articles (`items`), avec le taux de TVA de chaque ligne | Oui, sans réserve |
| **Tableur (.xlsx)** | Entêtes uniquement : 33 colonnes, une ligne par facture, aucun article | Partiellement (voir plus bas) |
| **PDF** | La facture certifiée, une par fichier, montants arrondis au franc | Non (voir plus bas) |

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

Sans article, le connecteur reconstitue les lignes à partir des totaux de chaque facture.

Le **taux effectif** (total TVA ÷ total HT) tranche entre deux cas.

S'il correspond à un taux pratiqué par l'entreprise, la facture ne porte qu'un seul taux : une ligne
suffit — quantité 1, prix unitaire = total HT, portée par l'article de ce taux.

Sinon la facture mélange deux taux, et le taux effectif dit **lesquels** : ce sont les deux taux qui
l'encadrent. Une entreprise qui facture à 18 % et à 9 % produit des taux effectifs entre les deux ;
un mélange de taxable et d'exonéré donne un taux effectif sous le taux le plus bas. Le partage entre
ces deux paliers se retrouve alors exactement, sans rien deviner :

```
HT(taux haut) = (100 × total TVA − taux bas × total HT) ÷ (taux haut − taux bas)
HT(taux bas)  = total HT − HT(taux haut)
```

Le calcul porte sur les totaux, jamais sur le taux effectif arrondi : sur un écart de neuf points,
un centième de point déplace des dizaines de francs.

### Pourquoi 18 / 9 et non 18 / exonéré

Les deux hypothèses sont numériquement possibles pour un même taux effectif — c'est la liste des
taux pratiqués par l'entreprise qui tranche, et elle se règle dans l'application.

Sur l'export de contrôle fourni (50 factures), 14 donnent un taux effectif intermédiaire. Le
partage 18 / 9 place la part à 18 % sur un **nombre entier d'unités** du prix unitaire certifié
(1 077,2763 F) — 16, 20, 40, 100, 120, 140 et 160 unités — pour les 14 factures. Le partage
18 / exonéré ne tombe juste pour aucune. L'entreprise facture bien à deux taux.

Ces factures sont récapitulées dans un **tableau**, trié par part au taux le plus bas, et non par un
avertissement répété facture par facture : le partage se vérifie en comparant les lignes entre
elles, un cas atypique se voyant alors immédiatement.

**Le format d'import ne transportant pas la taxe, c'est la fiche article Sage qui donne son régime à
chaque ligne.** Il faut donc **un article par taux** — sinon Sage appliquerait le même régime à
toutes les parts, et une facture à 9 % repartirait taxée à 18 %. Le connecteur le signale quand
deux parts se retrouvent sur le même article.

L'export JSON reste préférable : il porte le détail réel de chaque article, là où le tableur ne
permet qu'une reconstitution — exacte sur le partage entre deux taux, mais qui ne dit rien des
articles eux-mêmes.

## Export PDF — le document légal, pas une source d'import

Le PDF d'une facture certifiée est techniquement lisible : ses polices Montserrat sous-ensemblées
portent une table `ToUnicode`, et une extraction par police restitue proprement le texte.

Il reste inutilisable pour alimenter Sage, pour une raison qui n'a rien de technique : **tous ses
montants sont arrondis au franc à l'impression.** Sur la facture `2304903U26000000889` :

| Donnée | PDF | JSON (valeur certifiée) | Écart |
| --- | --- | --- | --- |
| Prix unitaire HT | `1 077` | `1077,2763` | 0,2763 |
| Total HT | `21 546` | `21545,526` | 0,474 |
| TVA | `3 878` | `3878,19468` | 0,195 |
| Total TTC | `25 424` | `25423,72068` | 0,279 |

Sur une seule ligne l'écart est négligeable ; répété sur chaque ligne de chaque facture, il fait
diverger la comptabilité de ce que la DGI a certifié. S'y ajoutent deux obstacles pratiques : un
fichier par facture, et des cellules coupées par le retour à la ligne (la référence `6FF001`
s'imprime sur deux lignes).

Le connecteur refuse donc explicitement les PDF, avec le message qui renvoie vers l'export JSON,
plutôt qu'une erreur de format générique.

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
