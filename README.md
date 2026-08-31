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
dotnet test                                    # 136 tests
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
dotnet run --project src/SageFne.Reader -- detail 1219           # relevé complet d'une pièce
dotnet run --project src/SageFne.Reader -- colonnes              # colonnes réelles des tables Sage
dotnet run --project src/SageFne.Reader -- taxes 1219            # paramétrage fiscal autour d'une pièce
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

`DL_CodeTaxe1` vaut « TVA » aussi bien à 9 % qu'à 18 % dans ce dossier, et la fiche
`F_TAXE` qui porte ce code est intitulée « TVA/VENTE » à 9 %. **C'est donc le taux porté
par la ligne qui tranche, jamais l'intitulé.** Les trois emplacements de taxe sont
examinés : rien ne garantit que la TVA soit en position 1 et l'AIRSI en position 2.

| Taux de la ligne | Code FNE | Où |
| --- | --- | --- |
| 18 % | `TVA` | `taxes` |
| 9 % | `TVAB` | `taxes` |
| 0 % | `TVAC` **ou** `TVAD` — voir plus bas | `taxes` |
| AIRSI 1,5 % | — | `customTaxes` |

Un taux positif hors nomenclature — 12 %, par exemple — n'est **pas** une exonération : il
est signalé et la ligne ne porte aucun code, plutôt que d'être certifiée à tort.

### La TVA à 0 % ne se devine pas

La nomenclature FNE distingue deux exonérations qui valent **toutes deux 0 %** :

- `TVAC` — exonération **conventionnelle**
- `TVAD` — exonération **légale**, TEE/RME

**Sage ne porte pas la différence.** Mapper automatiquement `DL_Taxe1 = 0` vers `TVAD`
reviendrait à déclarer à la DGI un régime fiscal qu'on ignore, sur une facture certifiée
qui ne se corrige plus que par un avoir. C'est interdit.

Le régime vient donc du paramétrage, de la règle la plus précise à la plus générale :

```jsonc
"Fne": {
  "ZeroVatCategoryByArticle":  { "13415001": "LegalExemptionTEE_RME" },  // 1. le produit
  "ZeroVatCategoryByCustomer": { "4111SITASARL": "ConventionalExemption" }, // 2. le client
  "ZeroVatCategory": "Unknown"                                           // 3. le dossier
}
```

Valeurs acceptées : `Unknown`, `ConventionalExemption`, `LegalExemptionTEE_RME` — ou
directement `TVAC` / `TVAD`. Une valeur mal orthographiée ne vaut pas classification : elle
bloque, plutôt que d'appliquer un régime approximatif.

### Chercher le discriminant dans le dossier

```bash
dotnet run --project src/SageFne.Reader -- taxes 1219
```

Montre, autour d'une pièce : **F_TAXE** en entier (toutes colonnes — un code EDI ou un
regroupement peut déjà porter le régime), les **colonnes de taxe brutes** de ses lignes,
la fiche **F_COMPTET** du client et les fiches **F_ARTICLE** de ses articles, colonnes
fiscales mises en avant.

Aucun nom de colonne n'est supposé : ils viennent de `sys.columns`. Les tables et colonnes
désignées passent par `IdentifiantSql`, qui n'accepte qu'une forme d'identifiant et refuse
tout le reste — c'est le seul endroit du projet où du texte entre dans une requête, les
valeurs restant toujours des paramètres.

La commande **montre, elle ne conclut pas**. Savoir si `CT_Classement` ou `FA_CodeFamille`
désigne un régime d'exonération dans ce dossier est une question fiscale, pas technique.

**Rien ne correspond → la pièce est bloquée**, avec une erreur explicite :

```
1219  [ERREUR ] ZERO_VAT_CATEGORY_UNKNOWN — ligne 1 : TVA 0 % détectée mais
      impossible de déterminer TVAC (exonération conventionnelle) ou
      TVAD (exonération légale TEE/RME).
```

Le relevé affiche alors `NON DETERMINE` en code FNE. **L'AIRSI part quand même** en
`customTaxes` : le prélèvement ne dépend pas du régime de TVA.

Une facture portant cette erreur ne peut pas être envoyée : elle n'entre ni dans `--json`,
ni dans `--sortie`, et le code de sortie vaut 1.

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

## Les types de documents, et pourquoi 6 et 7 sont la même facture

Relevé sur le dossier HT :

| DO_Type | Libellé | Documents | Traitement |
| --- | --- | --- | --- |
| 3 | Bon de livraison | 2 | **écarté** — rien n'y est dû |
| 4 | Bon de retour | 2 | **écarté** — appelle un avoir, pas une facture |
| 6 | Facture | 91 | candidate à la certification |
| 7 | Facture comptabilisée | 913 | candidate — **c'est la même facture qu'en 6** |

Les documents de type 7 portent tous `DO_DocType = 6`. C'est la signature d'un
changement d'état, pas d'un nouveau document : quand une facture est comptabilisée, Sage
fait passer `DO_Type` de 6 à 7 **sur la ligne existante** et laisse `DO_DocType` à 6, la
trace du type d'origine.

Deux conséquences.

**Le lot lit 6 et 7.** Ne lire que le 6 laisserait 913 factures sur 1 004 hors du champ.

**L'identité d'une facture ne peut pas s'appuyer sur `DO_Type`, qui bouge.** Le registre
des certifications est donc indexé sur `domaine / DO_DocType / DO_Piece` — par exemple
`0/6/1219`. Cette clé ne change pas à la comptabilisation : une facture certifiée en type 6
reste reconnue une fois passée en 7, et ne repart pas.

`DO_Piece` seul ne conviendrait pas non plus : Sage numérote par souche, et un bon de
livraison peut porter le même numéro qu'une facture sans être le même document.

### La vérification qui tranche

Si la comptabilisation modifiait la ligne au lieu de la remplacer, aucun numéro de pièce
ne devrait porter à la fois `DO_Type` 6 et 7. `doctypes` pose la question directement :

```
Un même numéro sous plusieurs types
───────────────────────────────────
  Aucun. Chaque numéro de pièce ne porte qu'un seul DO_Type.
```

Si des numéros portaient les deux, le lot refuserait de les envoyer (`PIECE_EN_DOUBLE`)
plutôt que de risquer une double certification.

### Les avoirs

`DO_Type = 4` n'est pas traité. Un bon de retour certifié comme une vente facturerait au
client ce qu'il vient de rendre : la logique des avoirs demande son propre travail, et
elle n'est pas écrite.

## Les colonnes ne sont pas les mêmes d'un dossier à l'autre

La liste des colonnes lues était écrite en dur. Le dossier HT n'a pas de
`DL_DocType` dans `F_DOCLIGNE`, et **toute la lecture des lignes échouait sur ce seul
nom** : `Invalid column name 'DL_DocType'`, au milieu d'un lot.

Les colonnes sont désormais demandées au catalogue avant la requête :

```sql
select c.name as Colonne
from sys.columns c
inner join sys.tables t on t.object_id = c.object_id
where t.name = @table
```

Ce qui existe est demandé, ce qui manque est laissé de côté et vaut son défaut à la
lecture. Un `db_datareader` suffit à lire `sys.columns`, et la requête passe par le même
`ReadOnlyGuard` que les autres. Le catalogue n'est lu qu'une fois par table et par
exécution.

Une colonne **indispensable** absente — `DL_Qte`, `DL_PrixUnitaire`, `DO_Piece`… — lève
en revanche une erreur qui la nomme, plutôt que de laisser passer un montant faux :

> La table F_DOCLIGNE du dossier ne porte pas DL_Qte, DL_PrixUnitaire. Ces colonnes sont
> indispensables à la lecture des factures.

```bash
dotnet run --project src/SageFne.Reader -- colonnes
```

liste, pour `F_DOCENTETE`, `F_DOCLIGNE` et `F_COMPTET`, ce que la table porte et ce qui
manque. `detail` le signale aussi en tête de son relevé.

**F_DOCLIGNE n'a aucun équivalent de `DO_DocType`.** Le type d'origine d'un document se lit
sur l'entête, `F_DOCENTETE.DO_DocType`, et nulle part ailleurs. Les lignes se rattachent à
leur entête par `DO_Domaine`, `DO_Piece` et `DO_Type`, qui existent bien dans les deux
tables.

## Le relevé d'une pièce

```bash
dotnet run --project src/SageFne.Reader -- detail 1219
```

Tout ce que porte la pièce, d'un côté Sage et de l'autre FNE : les documents qui partagent
son numéro et lesquels sont retenus, le client et son NCC, chaque ligne avec sa quantité,
son prix unitaire brut, sa remise, son prix net, son taux de TVA, son code FNE et son
AIRSI, les totaux recalculés depuis les lignes face à ceux de l'entête, **les champs FNE
obligatoires encore manquants**, les valeurs supposées faute de source dans Sage, et le
JSON qui partirait.

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
│   │                                    SageCustomer, SageTax, SageRemise,
│   │                                    SageDocumentTypes, SageDocumentTypeSummary,
│   │                                    SageDocumentDuplicate
│   ├── Models/Fne/                      FneInvoice, FneInvoiceItem, FneCustomTax
│   ├── Data/                            ISageInvoiceRepository, ISageTaxInspector,
│   │                                    SageInvoiceRepository, IdentifiantSql,
│   │                                    DemoSageInvoiceRepository, InvoiceQuery,
│   │                                    CritereSql, ReadOnlyGuard, ColonnesTable
│   ├── Mapping/                         IFneInvoiceMapper, FneInvoiceMapper,
│   │                                    TaxMapping, RemiseMapping,
│   │                                    RegimeTvaZero, ZeroVatClassifier
│   └── Validation/                      InvoiceValidator, FinancialChecks,
│                                        FneCompleteness, CheckReport
└── tests/SageFne.Reader.Tests/          mapping des taxes, contrôles, lecture par lot,
                                        ligne de commande, garde-fou SQL
```

## Prochaines étapes, pas encore faites

- Avoirs : `DO_Type = 4` (bon de retour) est écarté. Leur traitement reste à écrire.
- `pointOfSale` et `establishment` : à renseigner dans `appsettings.json`.
- Mode de règlement : figé à `deferred`, faute de source dans Sage.
- Remise d'entête (`DO_Remise`) : les remises de ligne sont lues, celle du document non.
- L'envoi vers `/external/invoices/sign` n'est pas écrit — et avec lui, l'inscription au
  registre des certifications.
