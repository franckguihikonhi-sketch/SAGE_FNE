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
dotnet test                                    # 22 tests
dotnet run --project src/SageFne.Reader        # dry run de la pièce 1219
dotnet run --project src/SageFne.Reader -- 1220   # une autre pièce
```

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
base, calqué sur la pièce 1219 relevée dans le dossier. Le mapping et les contrôles
s'exécutent réellement ; seule la lecture SQL est court-circuitée. Dès que la chaîne est
en place, c'est la base qui parle.

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
| 0 % | *(rien)* | — |
| AIRSI 1,5 % | `AIRSI` | `customTaxes` |

Les **trois** emplacements de taxe de Sage sont examinés (`DL_Taxe1/2/3`) : rien ne
garantit que la TVA restera en position 1 et l'AIRSI en position 2.

Une TVA n'est jamais inventée : une ligne sans taux part sans code, et le contrôle le
signale.

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
│   ├── Configuration/FneOptions.cs
│   ├── Models/Sage/                     SageDocumentHeader, SageDocumentLine,
│   │                                    SageCustomer, SageTax
│   ├── Models/Fne/                      FneInvoice, FneInvoiceItem, FneCustomTax
│   ├── Data/                            ISageInvoiceRepository, SageInvoiceRepository,
│   │                                    DemoSageInvoiceRepository, ReadOnlyGuard
│   ├── Mapping/                         IFneInvoiceMapper, FneInvoiceMapper, TaxMapping
│   └── Validation/                      InvoiceValidator, FinancialChecks, CheckReport
└── tests/SageFne.Reader.Tests/          mapping des taxes, contrôles, garde-fou SQL
```

## Prochaines étapes, pas encore faites

- Types de document : seul `DO_Type = 6` est accepté. Les autres restent à confirmer.
- Exonérations : FNE distingue `TVAC` et `TVAD`. À trancher avec la DGI.
- Remises : lire `DL_Remise0N_REM_Type` pour interpréter la valeur.
- `pointOfSale` et `establishment` : à renseigner dans `appsettings.json`.
- Mode de règlement : figé à `deferred`, faute de source dans Sage.
- L'envoi vers `/external/invoices/sign` n'est pas écrit.
