# SageFne.Reader

Première étape du middleware FNE pour Sage 100c V12 : **lire une facture dans Sage,
la traduire au format JSON de la DGI, et l'afficher**. Rien d'autre.

- Accès SQL **strictement en lecture** : uniquement des `SELECT` paramétrés.
- **Aucun appel** à l'API FNE de la DGI, ni à Internet.
- **Aucune écriture** dans la base Sage.

Un garde-fou (`ReadOnlyGuard`) refuse toute requête qui ne commence pas par `SELECT`,
qui contient un mot-clé d'écriture, ou qui enchaîne deux instructions. Une modification
distraite échoue avant d'atteindre le serveur.

## Commandes

```bash
dotnet restore
dotnet build
dotnet test                                    # 39 tests
```

Le dry run lit **un lot** de factures :

```bash
dotnet run --project src/SageFne.Reader                          # toutes les pièces, dans la limite
dotnet run --project src/SageFne.Reader -- 1219                  # une pièce, avec son JSON
dotnet run --project src/SageFne.Reader -- 1219 1220 1221        # plusieurs pièces
dotnet run --project src/SageFne.Reader -- --du 2025-12-01 --au 2025-12-31
dotnet run --project src/SageFne.Reader -- --du 2025-12-01 --sortie sorties/
```

| Option | Effet |
| --- | --- |
| `--du`, `--au` | Période, **bornes comprises** — « au 31 décembre » inclut les pièces datées du 31 à 23 h |
| `--limite N` | Nombre maximal de pièces, 500 par défaut |
| `--sortie DOSSIER` | Écrit un fichier JSON par pièce |
| `--json` | Affiche le JSON de chaque pièce, et pas seulement le résumé |

Une seule pièce demandée : son JSON s'affiche. Un lot : le résumé s'affiche, et le JSON
seulement si vous le demandez — sinon la console devient illisible.

Le code de sortie vaut 1 dès qu'une pièce est bloquée : de quoi enchaîner dans un script.

## Où renseigner la connexion SQL

`src/SageFne.Reader/appsettings.json` porte le gabarit, avec des valeurs factices :

```json
"ConnectionStrings": {
  "Sage": "Server=SERVEUR_SQL;Database=HT;User Id=UTILISATEUR;Password=MOT_DE_PASSE;TrustServerCertificate=True;"
}
```

**Ne remplacez pas le mot de passe dans ce fichier** : il est suivi par Git. Passez par
les secrets utilisateur, qui vivent hors du dépôt, dans votre profil Windows :

```bash
cd src/SageFne.Reader
dotnet user-secrets init          # déjà fait : UserSecretsId est dans le .csproj
dotnet user-secrets set "ConnectionStrings:Sage" "Server=MON-SERVEUR\SAGE;Database=HT;User Id=lecteur_fne;Password=…;TrustServerCertificate=True;"
dotnet user-secrets list
```

Sur Windows, ces secrets sont écrits dans
`%APPDATA%\Microsoft\UserSecrets\sagefne-reader-ht\secrets.json`.

Avec une authentification Windows plutôt qu'un compte SQL :

```
Server=MON-SERVEUR\SAGE;Database=HT;Integrated Security=True;TrustServerCertificate=True;
```

**Tant que la chaîne n'est pas renseignée**, le dry run tourne sur un jeu d'essai hors
base : la pièce 1219 relevée dans le dossier, et trois pièces bâties autour d'elle pour
couvrir une TVA à 18 %, une TVA à 9 % avec prélèvement, et un client sans NCC. Le mapping
et les contrôles s'exécutent réellement ; seule la lecture SQL est court-circuitée. Dès que
la chaîne est en place, c'est la base qui parle.

### Le compte SQL

Créez-lui un accès en lecture seule sur la base `HT` — c'est la garantie qui ne dépend
pas de notre code :

```sql
-- À exécuter par votre DBA, pas par cette application.
CREATE LOGIN lecteur_fne WITH PASSWORD = '…';
USE HT;
CREATE USER lecteur_fne FOR LOGIN lecteur_fne;
ALTER ROLE db_datareader ADD MEMBER lecteur_fne;
```

## Le mapping des taxes

C'est le point le plus délicat du dossier.

`DL_CodeTaxe1` vaut « TVA » **aussi bien pour 9 % que pour 18 %**, et la fiche `F_TAXE`
qui porte ce code s'intitule « TVA/VENTE » à 9 % tandis que « TVA/ACHAT » porte 18 %.
L'intitulé et le code ne permettent donc pas de trancher : **c'est le taux porté par la
ligne qui décide.**

| Taux de la ligne | Code FNE | Où |
| --- | --- | --- |
| 18 % | `TVA` | `taxes` |
| 9 % | `TVAB` | `taxes` |
| Aucune TVA | `TVAD` | `taxes` |
| AIRSI 1,5 % | `AIRSI` | `customTaxes` |

Une ligne sans TVA n'est pas une ligne sans code : FNE attend un code
d'exonération. `TVAD` (exonération légale) est appliqué par défaut — c'est celui que
portent les factures certifiées du dossier. Un dossier exonéré par convention change
`Fne:ExemptionCode` en `TVAC` dans `appsettings.json`, sans toucher au code.

Les **trois** emplacements de taxe de Sage sont examinés (`DL_Taxe1/2/3`) : rien ne
garantit que la TVA restera en position 1 et l'AIRSI en position 2.

Une TVA n'est jamais inventée. Un taux positif que la nomenclature ne connaît pas — 12 %,
par exemple — n'est pas une exonération : la ligne ne part **ni** avec ce taux, **ni** en
`TVAD`, et le contrôle le signale.

## Un lot, trois lectures

Lire cinquante factures ne fait pas cent cinquante allers-retours vers SQL Server, mais
**trois** : les entêtes, puis toutes les lignes du lot, puis tous les clients. Le
regroupement se fait ensuite en mémoire. Sur un mois de facturation, c'est la différence
entre une seconde et une minute — et la base n'est pas tenue occupée pendant que le lot
défile. Un test le vérifie en comptant les appels au dépôt.

Au-delà de 500 pièces ou de 500 comptes tiers, les listes sont découpées en tranches :
SQL Server plafonne à 2 100 paramètres par commande.

**Une pièce en défaut n'interrompt pas le lot.** Elle ressort marquée « bloquée », les
autres sont traduites. Un comptable veut voir tout ce qui cloche en une fois, pas le
découvrir une erreur après l'autre.

Les lignes d'un lot sont rattachées à leur entête par `DO_Piece` **et** `DO_Type` : filtrer
sur le seul numéro ramènerait aussi les lignes d'un document d'un autre type portant le
même numéro. La lecture d'une pièce isolée, elle, suit la spécification d'origine et filtre
sur le domaine et le numéro seuls.

## Contrôles financiers

Pour chaque ligne, le montant est recalculé et comparé à ce que Sage a stocké, avec une
tolérance d'**1 FCFA** :

- `quantité × prix unitaire` contre `DL_MontantHT` ;
- `HT majoré des taux de la ligne` contre `DL_MontantTTC`.

Un écart **ne bloque pas** : il produit un constat lisible. `DO_TotalHT` valant 0 sur une
partie des documents, c'est le total des lignes qui fait foi, et l'écart avec l'entête
est signalé plutôt que traité comme une erreur.

Les remises (`DL_Remise01REM_Valeur`…) sont lues mais **pas interprétées** : Sage range
leur nature (pourcentage ou valeur) dans une colonne `_REM_Type` que nous ne lisons pas
encore. Quand une remise est présente, le contrôle du HT se déclare non concluant plutôt
que de valider à tort.

## Ce qui bloque avant traduction

`PIECE_INTROUVABLE`, `PIECE_VIDE`, `CLIENT_INTROUVABLE`, `NCC_MANQUANT` (obligatoire en
B2B), `SANS_LIGNE`, `DESIGNATION_VIDE`, `QUANTITE_INVALIDE`, `PRIX_NEGATIF`.

`AR_Ref` vide est admis — la désignation, elle, est exigée.

## Arborescence

```
SageFne.sln
├── src/SageFne.Reader/
│   ├── Program.cs                       dry run : lecture, mapping, JSON, contrôles
│   ├── appsettings.json                 gabarit de connexion et paramètres FNE
│   ├── appsettings.Development.json     réglages du poste (jamais de mot de passe)
│   ├── Batch/                           InvoiceBatchReader, InvoiceConversion,
│   │                                    InvoiceBatch, CommandLine
│   ├── Configuration/FneOptions.cs
│   ├── Models/Sage/                     SageDocumentHeader, SageDocumentLine,
│   │                                    SageCustomer, SageTax
│   ├── Models/Fne/                      FneInvoice, FneInvoiceItem, FneCustomTax
│   ├── Data/                            ISageInvoiceRepository, SageInvoiceRepository,
│   │                                    DemoSageInvoiceRepository, InvoiceQuery,
│   │                                    CritereSql, ReadOnlyGuard
│   ├── Mapping/                         IFneInvoiceMapper, FneInvoiceMapper, TaxMapping
│   └── Validation/                      InvoiceValidator, FinancialChecks, CheckReport
└── tests/SageFne.Reader.Tests/          mapping des taxes, contrôles, lecture par lot,
                                        ligne de commande, garde-fou SQL
```

## Prochaines étapes, pas encore faites

- Types de document : seul `DO_Type = 6` est accepté. Les autres restent à confirmer.
- Pièces déjà certifiées : rien ne les distingue encore, un lot les reprendrait.
- Remises : lire `DL_Remise0N_REM_Type` pour interpréter la valeur.
- `pointOfSale` et `establishment` : à renseigner dans `appsettings.json`.
- Mode de règlement : figé à `deferred`, faute de source dans Sage.
- L'envoi vers `/external/invoices/sign` n'est pas écrit.
