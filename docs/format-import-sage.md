# Format d'import Sage 100 Gestion Commerciale

## Principe

Sage 100 importe des documents de vente via **Fichier > Importer > Format paramétrable**. Le
format lui-même se définit dans **Fichier > Format import/export** et se matérialise par un
fichier `.imp`. Ce fichier décrit, zone par zone :

- le type de fichier (texte délimité ou longueur fixe) et le séparateur ;
- l'ordre des zones et leur longueur ;
- le format des dates et des nombres ;
- la distinction entre l'enregistrement d'entête du document et ses lignes.

Le connecteur reproduit ce paramétrage sous forme de **profil** (`src/lib/sage/profile.ts`).
Un profil est du pur JSON : il n'y a pas de logique métier à modifier pour s'adapter à un
nouveau client.

## Éléments à récupérer chez le client

1. Le fichier `.imp` du format d'import des documents de vente (ou une copie d'écran des zones
   paramétrées, dans l'ordre).
2. Un fichier d'import déjà utilisé et accepté par Sage, même sur une seule facture. C'est la
   référence la plus fiable.
3. La liste des comptes tiers (`CT_Num`) pour construire la table de correspondance clients.
4. La souche / le journal de vente à utiliser, s'ils doivent figurer dans le fichier.

## Points à valider

Les valeurs suivantes sont posées par défaut dans le profil et **doivent être confirmées** sur
le dossier cible :

| Élément | Valeur par défaut | Où |
| --- | --- | --- |
| Type de document facture (`DO_Type`) | `6` | `profile.documentTypes.facture` |
| Type de document avoir | `5` | `profile.documentTypes.avoir` |
| Séparateur de zones | tabulation | `profile.delimiter` |
| Encodage | Windows-1252 | `profile.encoding` |
| Fin de ligne | CRLF | `profile.eol` |
| Format de date | `DDMMYYYY` | `profile.dateFormat` |
| Séparateur décimal | `.` | `profile.decimalSeparator` |
| Marqueur entête / ligne | `E` / `L` | première colonne des layouts |
| Numéro de pièce | année + numéro FNE (`26000000889`) | option `numeroPiece` |
| Montants d'un avoir | valeurs positives | option `avoirEnValeurAbsolue` |
| Codes règlement | aucun | table de correspondance à saisir |

Nomenclature `DO_Type` de Sage 100 pour les ventes : `0` devis, `1` bon de commande,
`2` préparation de livraison, `3` bon de livraison, `4` bon de retour, `5` bon d'avoir financier,
`6` facture, `7` facture comptabilisée. Le choix entre `6` et `7` dépend du fait que la facture
doit rester modifiable ou arriver déjà comptabilisée.

## Numéro de pièce et référence FNE

Une référence FNE fait 19 caractères (`2304903U26000000889`), au-delà de la zone `DO_Piece` de
Sage. Par défaut seule la partie année + numéro d'ordre est transmise comme numéro de pièce
(`26000000889`, 11 caractères) : le NCC en préfixe est constant pour une entreprise. La référence
complète est toujours écrite dans la zone « Référence FNE » du fichier, pour la traçabilité et les
contrôles de la DGI.

Basculer sur `numeroPiece: "reference"` transmet la référence complète — à réserver aux dossiers
dont la zone a été étendue, sinon Sage tronquera.

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
