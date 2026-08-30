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
dotnet test                                    # 69 tests
```

Le dry run lit **un lot** de factures :

```bash
dotnet run --project src/SageFne.Reader                          # toutes les pièces, dans la limite
dotnet run --project src/SageFne.Reader -- 1219                  # une pièce, avec son JSON
dotnet run --project src/SageFne.Reader -- 1219 1220 1221        # plusieurs pièces
dotnet run --project src/SageFne.Reader -- --du 2025-12-01 --au 2025-12-31
dotnet run --project src/SageFne.Reader -- --du 2025-12-01 --sortie sorties/
```

Et un diagnostic, en lecture seule lui aussi :

```bash
dotnet run --project src/SageFne.Reader -- doctypes              # inventaire des types de documents
```

| Option | Effet |
| --- | --- |
| `--du`, `--au` | Période, **bornes comprises** — « au 31 décembre » inclut les pièces datées du 31 à 23 h |
| `--limite N` | Nombre maximal de pièces, 500 par défaut |
| `--sortie DOSSIER` | Écrit un fichier JSON par pièce |
| `--registre F` | Registre des certifications à consulter |
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

## Les remises

FNE reçoit un prix unitaire et une quantité, puis fait la multiplication. Envoyer le prix
**brut** d'une ligne remisée ferait donc certifier plus que ce que le client a payé — un
faux qui ne se corrige que par un avoir.

Sage porte trois remises en cascade par ligne, chacune avec sa valeur
(`DL_Remise0NREM_Valeur`) et son type (`DL_Remise0NREM_Type` : 0 pour un pourcentage,
1 pour un montant). **La valeur seule est ambiguë** : sur une ligne à 2 000, « 200 » vaut
1 800 si c'est un montant et 1 980 si c'est un pourcentage.

Le prix envoyé n'est pas recalculé depuis ces champs : il est **déduit de
`DL_MontantHT`**, le net que Sage a lui-même calculé, divisé par la quantité. Ce chiffre
est exact quelle que soit la lecture du type.

Le recalcul en cascade sert alors de **contrôle** : quand il retrouve le net de Sage, la
lecture des types est confirmée et le dry run l'écrit (`REMISE_APPLIQUEE`). Quand il tombe
sur autre chose, c'est notre lecture qui est fausse — le prix de Sage part quand même,
mais le constat `REMISE_NON_CONCORDANTE` le signale.

```
1223  [à noter] REMISE_APPLIQUEE — ligne 1 : remise 10 % sur 5000 —
      prix net 4500 envoyé, conforme au montant calculé par Sage.
```

Le champ `discount` de FNE reste à 0, la remise étant déjà dans le prix. À confirmer sur
la documentation DGI : si `discount` doit porter la remise pour l'afficher sur la facture
certifiée, c'est le mapping qui changera, pas le total.

Une remise portée par l'**entête** (`DO_Remise`) n'est pas encore lue.

## Les pièces déjà certifiées

Une facture envoyée deux fois à la DGI, c'est un doublon qui ne se rattrape pas. Le lot
doit donc savoir ce qui est déjà parti.

**Cette information ne peut pas vivre dans Sage** : la base y est en lecture seule, et
aucune zone n'y est prévue pour une référence FNE. Elle vit dans un **registre à nous**,
un fichier JSON à côté de l'application (`Fne:CertificationLedgerPath`, ou `--registre`).

Chaque pièce y est reconnue par son numéro **et par l'empreinte de ce qui a été envoyé** —
un SHA-256 du corps de requête. D'où quatre états, et non deux :

| État | Ce que ça veut dire |
| --- | --- |
| **à certifier** | Inconnue du registre, traduite et contrôlée : elle peut partir |
| **déjà certifiée** | Dans le registre, empreinte identique : ne pas renvoyer |
| **modifiée depuis** | Dans le registre, empreinte différente : Sage a changé après la certification |
| **bloquée** | Une erreur empêche de la traduire |

Le troisième état est le plus important. Une pièce certifiée puis modifiée dans Sage veut
dire que **la facture remise au client ne correspond plus au document** : il faut sans
doute un avoir puis une nouvelle facture. Ce n'est pas à l'outil d'en décider, mais c'est
à lui de le voir — il le signale en erreur et ne renvoie rien.

Seules les pièces « à certifier » sont publiées par `--json` et `--sortie`. Les autres
apparaissent au résumé, avec leur raison.

L'empreinte porte sur le **corps de requête**, pas sur les champs Sage : une modification
qui ne change rien à ce qui part — un champ que le mapping n'utilise pas — ne déclenche
pas d'alerte inutile.

Le registre s'écrit par fichier temporaire puis renommage : une coupure en plein
enregistrement laisse l'ancien registre intact plutôt qu'un fichier tronqué. Un registre
illisible est signalé et traité comme vide : mieux vaut proposer de recertifier, ce que
l'exploitant verra, qu'interrompre le traitement.

> Rien n'inscrit encore de certification : l'envoi n'est pas écrit. `RecordAsync` existe et
> est testé, il sera appelé à l'étape suivante. Le dry run hors base marque deux pièces
> lui-même, pour montrer les états.

## Quels types de documents ce dossier utilise ?

`DO_Type = 6` est le seul type traité pour l'instant. Encore faut-il vérifier que c'est
bien le bon dans **ce** dossier : le paramétrage varie d'une installation à l'autre, et
une facture comptabilisée ne porte pas le même type qu'une facture en cours.

```bash
dotnet run --project src/SageFne.Reader -- doctypes
```

La commande lit `F_DOCENTETE` avec `DO_Domaine = 0` et donne, pour chaque `DO_Type`
rencontré : le nombre de documents, le total TTC, la période couverte, et cinq
exemplaires — `DO_Piece`, `DO_Date`, `DO_Tiers`, `DO_TotalTTC`, et `DO_DocType` quand
la colonne existe dans le dossier.

**Elle ne filtre pas sur `DO_Type`** : ce serait répondre « 6 » à la question posée. Le
domaine borne la lecture, le type non.

Deux `SELECT` : un dénombrement groupé, puis les derniers documents de chaque type
numérotés par `row_number() over (partition by DO_Type ...)` — une seule lecture au lieu
d'une par type. S'y ajoute une consultation de `INFORMATION_SCHEMA.COLUMNS`, parce que
`DO_DocType` n'existe pas dans toutes les versions du dossier : mieux vaut demander au
catalogue que faire échouer le diagnostic pour l'apprendre. Les trois requêtes passent par
`ReadOnlyGuard`, comme les autres. Rien n'est écrit.

Sans chaîne de connexion, la commande tourne sur le jeu d'essai et le dit : les chiffres
affichés ne sont alors pas ceux du dossier.

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

Les lignes sont rattachées à leur entête par le domaine, `DO_Piece` **et** `DO_Type` :
filtrer sur le seul numéro ramènerait aussi les lignes d'un document d'un autre type
portant le même numéro — un bon de livraison 1219 en même temps que la facture 1219.

**Une pièce isolée et un lot suivent la même règle** : `GetInvoiceLinesAsync` passe par la
lecture de lot avec un critère d'une seule pièce. Une seule requête à maintenir, et pas
deux comportements à réconcilier. Un test compare les deux textes SQL pour que cela le
reste.

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
│   ├── Certification/                   ICertificationLedger, JsonCertificationLedger,
│   │                                    CertifiedInvoice, InvoiceFingerprint
│   ├── Configuration/FneOptions.cs
│   ├── Models/Sage/                     SageDocumentHeader, SageDocumentLine,
│   │                                    SageCustomer, SageTax,
│   │                                    SageDocumentTypeSummary
│   ├── Models/Fne/                      FneInvoice, FneInvoiceItem, FneCustomTax
│   ├── Data/                            ISageInvoiceRepository, SageInvoiceRepository,
│   │                                    DemoSageInvoiceRepository, InvoiceQuery,
│   │                                    CritereSql, ReadOnlyGuard
│   ├── Mapping/                         IFneInvoiceMapper, FneInvoiceMapper,
│   │                                    TaxMapping, RemiseMapping
│   └── Validation/                      InvoiceValidator, FinancialChecks, CheckReport
└── tests/SageFne.Reader.Tests/          mapping des taxes, contrôles, lecture par lot,
                                        ligne de commande, garde-fou SQL
```

## Prochaines étapes, pas encore faites

- Types de document : seul `DO_Type = 6` est traité. `doctypes` montre ce que le dossier
  utilise réellement ; les autres types restent à confirmer avant d'être acceptés.
- `pointOfSale` et `establishment` : à renseigner dans `appsettings.json`.
- Mode de règlement : figé à `deferred`, faute de source dans Sage.
- Remise d'entête (`DO_Remise`) : les remises de ligne sont lues, celle du document non.
- L'envoi vers `/external/invoices/sign` n'est pas écrit — et avec lui, l'inscription au
  registre des certifications.
