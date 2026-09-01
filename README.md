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
dotnet test                                    # 459 tests
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
dotnet run --project src/SageFne.Reader -- apercu 1052           # aperçu FNE, aucune API contactée
dotnet run --project src/SageFne.Reader -- colonnes              # colonnes réelles des tables Sage
dotnet run --project src/SageFne.Reader -- taxes 1219            # paramétrage fiscal autour d'une pièce
dotnet run --project src/SageFne.Reader -- candidats-fne         # factures d'essai fiscalement nettes
dotnet run --project src/SageFne.Reader -- fne-check             # vérifie l'accès FNE, sans rien appeler
dotnet run --project src/SageFne.Reader -- envoyer 1052          # montre la requête, n'envoie rien
dotnet run --project src/SageFne.Reader -- envoyer 1052 --confirmer   # envoie pour de vrai
dotnet run --project src/SageFne.Reader -- statut 1052           # ce que le registre sait d'une pièce
dotnet run --project src/SageFne.Reader -- registre-info         # où vit le registre, ce qu'il contient
dotnet run --project src/SageFne.Reader -- reparer-source 1052   # origine d'une entrée ancienne
dotnet run --project src/SageFne.Reader -- debloquer 1052 --non-certifiee --confirmer
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

**Sage ne porte pas la différence**, et la vérification faite sur le dossier HT l'a
confirmé : `F_TAXE` n'a aucune fiche à taux 0, et une ligne exonérée ne porte aucun code
de taxe — `DL_CodeTaxe1` vide, `DL_Taxe1 = 0`. La distinction est un fait juridique que
Sage n'a jamais eu de raison d'enregistrer.

Elle se déclare donc, du plus précis au plus général :

```jsonc
"Fne": {
  "ZeroVat": {
    "ByArticle":  { "13415001": "LegalExemptionTEE_RME" },   // 1. le produit
    "ByFamily":   { "02": "LegalExemptionTEE_RME" },         // 2. sa famille
    "ByCustomer": { "4111SITASARL": "ConventionalExemption" },// 3. le client
    "Default": "Unknown"                                     // 4. le dossier
  }
}
```

| Priorité | Clé | D'où elle vient |
| --- | --- | --- |
| 1 | `ByArticle` | `AR_Ref` de la ligne |
| 2 | `ByFamily` | `FA_CodeFamille` de l'article, lu dans `F_ARTICLE` |
| 3 | `ByCustomer` | `CT_Num` du client de la pièce |
| 4 | `Default` | le dossier entier |
| 5 | — | **`ZERO_VAT_CATEGORY_UNKNOWN`**, pièce bloquée |

**Deux valeurs, et deux seulement** : `ConventionalExemption` et `LegalExemptionTEE_RME`
(plus `Unknown`, qui bloque volontairement). Écrire `TVAD` ou `legale` n'est pas accepté —
et surtout **pas ignoré en silence** : la règle est refusée par `ZERO_VAT_CATEGORY_INVALID`,
sans passer au niveau suivant. Traiter une faute de frappe comme une absence de règle ferait
partir la facture sous un régime que personne n'a voulu.

Le relevé montre quelle règle a décidé :

```
Classification des lignes à 0 % de TVA
  Ligne Article        Famille    Règle appliquée              Régime
      1 13415001       02         aucune règle applicable      non déterminé
```

L'**AIRSI part quand même** en `customTaxes` : un prélèvement ne dépend pas du régime de TVA.

### Où viennent les règles, plus tard

`IZeroVatPolicy` est une interface, et `ConfiguredZeroVatPolicy` n'en est qu'une
implémentation — celle qui lit `appsettings.json`. Un écran de paramétrage SaaS
alimentera le même contrat sans que le mapping change. `ZeroVatOptions` est plat à
dessein : quatre dictionnaires de chaînes, qui se sérialisent tels quels.

### Les prélèvements ne se devinent pas non plus

Les trois fiches de `F_TAXE` du dossier portent **toutes** `TA_EdiCode = "VAT"`, AIRSI
compris. Se fier à ce champ ferait certifier l'AIRSI comme de la TVA. `TA_Regroup`, lui,
sépare correctement — « TVA » pour les deux taux, « AIRSI » pour le prélèvement — et sert
à nommer le groupe d'un code.

Mais il ne décide pas seul. Un code n'entre en `customTaxes` que s'il est **explicitement
mappé** :

```jsonc
"Fne": { "CustomTaxes": { "AIRSI": "AIRSI" } }
```

Un code non mappé rangé dans le même groupe qu'un prélèvement repris est signalé par
`PRELEVEMENT_SANS_MAPPING_FNE` et bloque la pièce, plutôt que de partir sous un nom que
personne n'a validé :

```
ligne 1 : le code « AIB » (2 %) appartient au regroupement « AIRSI », comme AIRSI
qui est repris en customTaxes. Aucun nom FNE ne lui est associé : ajoutez-le à
Fne:CustomTaxes plutôt que de le laisser deviner.
```

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

## Choisir la facture du premier envoi

```bash
dotnet run --project src/SageFne.Reader -- candidats-fne
```

Le premier envoi à la DGI est irréversible : ce qui part certifié ne se corrige que par un
avoir. La pièce d'essai doit donc être la moins discutable du dossier. La commande passe
tout le domaine des ventes par la **conversion réelle** — même mapping, mêmes contrôles que
ce qui partirait — puis note chaque facture et propose un meilleur candidat par taux.

**Ce qui écarte sans appel :**

| Motif | Pourquoi |
| --- | --- |
| une ligne à 0 % de TVA | régime `TVAC`/`TVAD` non tranché : une pièce d'essai ne doit soulever aucune question |
| un taux hors nomenclature | 12 % n'existe pas côté FNE |
| NCC absent | la facture ne peut pas partir en B2B |
| une erreur de contrôle | quelle qu'elle soit |
| déjà connue du registre | certifiée, ou modifiée depuis |
| pas de ligne au taux cherché | ce n'est pas un candidat pour ce taux |

**Ce qui départage**, par ordre de poids : aucun constat du tout (+100), total TTC des
lignes conforme à `DO_TotalTTC` (+60), taux unique (+40), peu de lignes (+40 pour une
seule), aucun prélèvement (+15). Chaque point gagné ou perdu est écrit en clair sous le
candidat — un candidat qu'on ne comprend pas n'en est pas un.

Quand **aucune** facture ne passe pour un taux, la commande recense les motifs plutôt que
d'en montrer cinq au hasard, et détaille `ERREURS_CONTROLE` par constat — sans quoi on ne
sait pas s'il faut corriger une fiche client ou une quantité :

```
  Sur 151 pièces portant du 18 %, voici ce qui les écarte :

    ERREURS_CONTROLE              151 pièces
      dont NCC_MANQUANT           145 pièces
      dont QUANTITE_INVALIDE        4 pièces
    NCC_ABSENT                    145 pièces
    LIGNE_TVA_ZERO                 28 pièces
```

Elle montre ensuite les pièces **dont le NCC est renseigné** — les plus proches du but,
puisqu'il ne leur reste qu'un défaut qui n'est pas dans la fiche client.

Enfin, elle liste les comptes dépourvus de `CT_Identifiant` avec un **cumul** : quelques
comptes portent souvent l'essentiel du volume, et c'est par eux qu'il faut commencer.
C'est dans Sage que cela se corrige, pas ici.

Pour le meilleur de chaque taux, la fiche donne `DO_Piece`, `DO_Type`, `DO_DocType`,
`DO_Date`, `DO_Tiers`, `CT_Intitule`, le NCC, le nombre de lignes, les taux rencontrés, les
`customTaxes`, les totaux HT et TTC recalculés face à `DO_TotalTTC`, l'écart, et le statut.

## Comprendre les ventes à 0 % avant de les paramétrer

```powershell
dotnet run --project src\SageFne.Reader -- audit-tva-zero
```

**Cette commande ne classe rien.** Elle ne dit ni `TVAC` ni `TVAD`, et ne le peut pas : les
deux valent 0 %, et Sage ne porte pas la différence. Un test vérifie qu'aucun de ces codes
n'apparaît nulle part dans son résultat.

Elle expose des faits. Par article : désignation, famille, lignes à 0 %, factures, clients
avec leur NCC, quantités et montants cumulés, les codes `DL_CodeTaxe1/2/3` et taux
`DL_Taxe1/2/3` réellement observés sur les lignes à 0 %, les pièces exemples, et surtout
**les autres taux auxquels ce même article est vendu ailleurs**.

C'est cette dernière colonne qui informe. Un article jamais vendu autrement qu'à 0 % relève
d'une règle attachée à l'article ; un client qui n'achète jamais taxé, d'une règle attachée
au client. Un article panaché — tantôt 0 %, tantôt 18 % — signale que la règle est
ailleurs : dans l'opération, ou dans une saisie à vérifier. Les tableaux par famille et par
client donnent la même lecture, en comparant lignes à 0 % et lignes taxées côte à côte.

### Restreindre l'affichage sans réduire l'analyse

La sortie complète est volumineuse. Trois filtres la resserrent :

```powershell
dotnet run --project src\SageFne.Reader -- audit-tva-zero --article 25SN001
dotnet run --project src\SageFne.Reader -- audit-tva-zero --famille 01
dotnet run --project src\SageFne.Reader -- audit-tva-zero --client 4111SOGEL
```

**L'analyse reste entière** : elle porte toujours sur tout le périmètre lu, et les totaux
du résumé restent ceux du dossier. Seul l'affichage est réduit, et la commande le dit en
tête pour qu'on ne prenne pas un extrait pour un total.

`--article` ajoute le relevé complet de cet article — **ventes taxées comprises**, ce que
l'inventaire d'ensemble ne montre pas. Ligne par ligne : pièce, date, taux de TVA effectif,
quantité, HT, client, NCC, et les trois emplacements `DL_CodeTaxe`/`DL_Taxe` tels quels.
Puis la répartition par taux, et une lecture en un mot : *jamais vendu taxé*, *panaché*, ou
*jamais vendu à 0 %*.

C'est cette lecture qui répond à la question posée, sans jamais la dépasser : savoir qu'un
article est panaché dit que le 0 % ne tient pas à l'article — cela ne dit toujours pas s'il
relève de `TVAC` ou de `TVAD`.

Le fondement juridique se déclare ensuite dans `Fne:ZeroVat`, par article, famille, client
ou dossier. Tant qu'il manque, les pièces concernées restent bloquées par
`ZERO_VAT_CATEGORY_UNKNOWN`, et c'est voulu.

## L'envoi à la certification

**Une facture certifiée ne s'annule pas** : elle se corrige par un avoir. `envoyer` **simule
par défaut** — elle affiche la requête exacte, adresse et en-têtes compris, et s'arrête.
Seul `--confirmer` déclenche l'appel.

Trois refus avant même la requête : le jeu d'essai (une facture inventée ne s'envoie pas à
la DGI), un accès non configuré, une pièce qui n'est pas « à certifier ».

### Ce que le registre autorise à repartir

L'état inscrit au registre décide, autant que l'empreinte :

| État au registre | La pièce | Pourquoi |
|---|---|---|
| *aucune trace* | part | jamais envoyée |
| `Error` | repart | la plateforme a refusé : rien n'a été certifié |
| `Sending` | **bloquée** | l'envoi est parti, son issue est inconnue |
| `Certified`, empreinte identique | bloquée | déjà certifiée, inchangée |
| `Certified`, empreinte différente | bloquée | certifiée puis modifiée dans Sage : un avoir s'impose |

Un refus de la DGI ne condamne donc pas la facture — elle repart une fois la cause
corrigée. Un envoi dont la réponse s'est perdue, en revanche, ne repart jamais tout seul :
la DGI l'a peut-être enregistré, et un doublon ne se rattrape pas.

Une réponse **5xx** compte comme une issue inconnue, pas comme un refus : la plateforme a
pu enregistrer la facture avant d'échouer. Un **4xx** est net — la requête a été rejetée,
rien n'a été créé.

### Le registre est la seule mémoire d'une certification

Sage n'en porte aucune trace : l'accès y est en lecture seule, et rien n'y prévoit de zone
pour une référence FNE. **Perdre ce fichier fait repartir à la DGI des factures déjà
certifiées**, et un doublon ne se corrige que par un avoir.

Il vit donc dans les données d'application de l'utilisateur — `%APPDATA%\SageFne\` sous
Windows — et non plus à côté de l'exécutable. Ce dernier emplacement était une faute :
`bin\Debug\net8.0\` est une sortie de compilation, que `dotnet clean`, une suppression de
`bin` ou un clone neuf effacent sans prévenir. **Une certification réelle y a été perdue.**

```powershell
dotnet run --project src\SageFne.Reader -- registre-info
```

Chemin absolu, origine de ce chemin, présence du fichier, taille, date de dernière
modification, nombre d'entrées et leur liste, environnement TEST ou PRODUCTION. Aucune clé
n'y figure — la commande ne lit pas la configuration d'accès. Si un registre subsiste à
l'ancien emplacement, elle le signale sans rien déplacer.

**Un registre illisible arrête tout.** Il fut un temps traité comme vide, et c'était le
défaut à ne pas commettre : « vide » veut dire « rien n'a jamais été certifié ». Un fichier
tronqué rendait donc envoyable l'intégralité des factures déjà certifiées. Toute commande
refuse désormais de s'exécuter et renvoie à `registre-info`, seule commande à savoir
décrire un registre qu'elle n'utilise pas.

### Rattraper une certification dont la trace manque

Quand une facture est certifiée sans que le registre l'ait su — registre perdu, envoi passé
par un autre outil, réponse égarée — elle repartirait au prochain envoi.

```powershell
dotnet run --project src\SageFne.Reader -- reconcilier 1052 `
  --reference "2304903U26000000930" --token "…" --confirmer
```

Aucune API n'est appelée : nous n'avons pas de quoi interroger la DGI sur une facture. La
référence vient de l'exploitant, qui l'a relevée sur le portail ou sur le PDF. La trace le
dit en toutes lettres, pour que personne ne la prenne plus tard pour un aller-retour
automatique.

Sans `--reference`, la commande refuse. Sans `--confirmer`, elle montre ce qu'elle
inscrirait et s'arrête. `--token` est facultatif : tous les PDF ne le portent pas. Une
pièce déjà `Certified` n'est jamais réécrite.

Une pièce que nos propres contrôles bloquent — un NCC manquant, par exemple — se réconcilie
tout de même. Si la DGI l'a certifiée, le refuser la laisserait envoyable, ce qui est
précisément le danger : la réalité constatée l'emporte sur notre opinion du document.

L'empreinte inscrite est celle du document **tel qu'il est aujourd'hui**, et non celle du
corps réellement envoyé, qui est perdu avec la trace. Si la pièce a changé dans Sage depuis
sa certification, la réconciliation grave cette version-là. C'est le prix du rattrapage, et
la commande le dit.

### Un 5xx ne dit pas que la DGI n'a rien enregistré

**La plateforme d'essai de la DGI certifie des factures en répondant 500**, et son portail
ne les publie pas immédiatement. Un opérateur qui va vérifier dans la minute qui suit
l'échec voit une absence qui n'en est pas une.

C'est ainsi qu'un doublon réel a été créé sur la pièce 1072 : envoi → 500 → portail
consulté, facture introuvable → déclarée non certifiée → renvoi → 500 → le portail montre
finalement **deux** factures. Les deux envois avaient abouti.

Rien n'a alerté au second envoi, parce que rien n'y survivait du premier : la trace était
reconstruite à neuf à chaque envoi, et affirmait donc « cette pièce n'est jamais partie ».
Trois choses en découlent.

**Le registre tient un journal en ajout seul**, reporté d'une écriture à l'autre : chaque
POST parti, chaque réponse avec son code HTTP, chaque décision d'opérateur. `statut`
l'affiche, et signale en capitales quand plus d'un envoi est parti. `envoyer` le rappelle
avant tout renvoi.

**Déclarer une pièce non certifiée exige un motif et du temps.** C'est la seule décision
qui rouvre un envoi, donc la seule qui puisse créer un doublon. Elle refuse tant que
`Fne:PortalCheckDelayMinutes` (15 par défaut) ne s'est pas écoulé depuis l'envoi, et
rappelle alors de revérifier le portail. Ce délai ne garantit rien — nul ne connaît la
latence réelle du portail — il empêche seulement la vérification réflexe.

**Constater la présence, elle, n'attend pas** : c'est une preuve positive, qui ferme
l'envoi au lieu de le rouvrir. `debloquer` offre donc trois constats exclusifs :

```powershell
# elle y figure, sous ce numéro
dotnet run --project src\SageFne.Reader -- debloquer 1072 --reference "REF" --confirmer

# elle y figure, sans numéro publié — le cas de la plateforme d'essai
dotnet run --project src\SageFne.Reader -- debloquer 1072 --sans-reference `
  --motif "Constatee au portail, aucun numero publie" --confirmer

# elle n'y figure pas — exige --motif, et le délai depuis l'envoi
dotnet run --project src\SageFne.Reader -- debloquer 1072 --non-certifiee `
  --motif "Absente du portail, verifie a 09h15" --confirmer
```

Le classement conserve tout : identité, empreinte, réponse HTTP d'origine, journal des
tentatives. Aucune référence n'est inventée, et la pièce ne repart plus jamais.

### Reconstituer une histoire qui n'a jamais été écrite

Les envois antérieurs au journal n'ont laissé aucune trace. Cette histoire n'a pas été
perdue : elle n'a jamais été écrite, et rien ne permet de la déduire. La déduire serait la
pire des réponses — **un journal inventé vaut moins qu'un journal vide, parce qu'on le
croit.**

Ce que l'exploitant sait, lui, peut être inscrit :

```powershell
dotnet run --project src\SageFne.Reader -- journal 1072 `
  --ajouter "POST n° 1, HTTP 500, issue inconnue" --quand "2026-08-31 23:40" `
  --code-http 500 --confirmer
```

L'entrée porte le genre **reconstitué**, qui la sépare pour toujours d'un fait observé, et
la date des faits plutôt que celle de sa saisie. Le stockage garde l'ordre d'écriture —
c'est lui, la trace — tandis que `statut` affiche la chronologie, si bien qu'un événement
reconstitué se lit à sa place et non à la fin.

La date est obligatoire : sans elle, l'entrée se rangerait au présent et fausserait la
chronologie qu'elle sert à rétablir. Une date à venir est refusée. Rien d'autre ne bouge :
ni l'état, ni l'identité, ni l'empreinte, ni la référence.

Côté base d'audit, `certification_tentatives` porte le même journal, en ajout seul par
déclencheur, sans politique d'`update` ni de `delete`. Un fait observé ne peut pas y être
antidaté ; une reconstitution le peut, et c'est tout son objet.

### Une certification peut ne porter aucune référence

La plateforme d'essai de la DGI certifie des factures **sans publier de référence
exploitable** : ni le PDF ni la fiche du portail n'en montrent. Exiger un numéro poussait
donc à en inventer un, et c'est arrivé — une valeur d'exemple a été inscrite telle quelle.
Une référence inventée est pire que pas de référence : elle désigne chez la DGI une facture
qui n'existe pas.

```powershell
dotnet run --project src\SageFne.Reader -- reconcilier 1052 `
  --sans-reference --motif "Aucune référence visible sur le portail/PDF TEST" --confirmer
```

`--sans-reference` est exigé plutôt que déduit de l'absence de `--reference` : sans ce
constat explicite, une faute de frappe passerait pour lui. Le motif est obligatoire — c'est
la seule chose qui restera, dans six mois, pour expliquer l'absence de numéro.

**L'absence de référence ne rend jamais une pièce envoyable.** C'est l'identité Sage
`domaine/DO_DocType/DO_Piece` qui bloque le renvoi, jamais le numéro de la DGI. Une pièce
`Certified` sans référence est aussi bloquée qu'une autre, et des tests le verrouillent.

### Une valeur par défaut ne doit jamais être une affirmation

`SourceCertification` disait d'où vient ce que le registre affirme : réponse de la DGI lue
par le middleware, ou référence relevée à la main sur le portail. `Middleware` en occupait
la première place — donc la valeur d'un champ absent — et un initialiseur de propriété la
posait en plus explicitement.

Une entrée écrite **avant l'existence du champ** se relisait donc « la DGI l'a dit » : la
plus forte affirmation que le champ sache porter, tirée d'une absence d'information. Une
réconciliation manuelle réelle s'est ainsi retrouvée classée réponse de plateforme, et
devenue **incorrigible** — les corrections étant réservées aux déclarations humaines, qui
seules peuvent être fautives.

`Inconnue` occupe maintenant la place zéro, et chaque écriture déclare sa source
explicitement : `Trace()` pose `Middleware` pour un envoi réel, `ReconcilierAsync` pose
`ReconciliationManuelle`. Des tests vérifient qu'aucune écriture ne laisse la source au
défaut.

### Établir l'origine d'une entrée ancienne

```powershell
dotnet run --project src\SageFne.Reader -- reparer-source 1052
```

Sans `--confirmer`, elle affiche la source actuelle, celle qu'elle propose, et sur quoi
elle se fonde. Rien n'est écrit.

La requalification ne repose que sur des **preuves internes à l'entrée**, et ne conclut
jamais qu'à la réconciliation manuelle : elle seule laisse une attestation textuelle sans
ambiguïté — « réconciliation manuelle », « constatée sur le portail DGI par l'exploitant »,
« non observée par le middleware », exigées **ensemble**. Prises isolément, ces mentions
peuvent figurer dans un motif saisi à la main.

Déduire « réponse de la plateforme » d'une absence de preuve serait refaire exactement
l'erreur qui rend cette commande nécessaire : une vraie réponse FNE n'est jamais reclassée,
et une ligne qui se déclare déjà n'est pas rediagnostiquée.

Seule la source change. Ni l'état, ni l'identité, ni l'empreinte, ni l'horodatage, ni la
référence — même fautive. Une copie du registre est prise avant écriture.

### Corriger une réconciliation fautive

```powershell
dotnet run --project src\SageFne.Reader -- corriger-reconciliation 1052 `
  --supprimer-reference --reference-actuelle "TA_REFERENCE_FNE" `
  --motif "Aucune référence FNE visible sur le portail/PDF TEST" --confirmer
```

La certification **n'est pas défaite** : seule la référence s'en va. L'état reste
`Certified`, l'identité, l'empreinte et l'horodatage d'origine ne bougent pas, et la pièce
demeure bloquée au renvoi — c'est tout l'enjeu.

`--reference-actuelle` est un verrou : vous déclarez ce que vous vous attendez à trouver, et
la commande refuse si le registre porte autre chose. Il a pu changer depuis que vous l'avez
lu. **Une copie horodatée du registre est prise avant écriture**, et jamais écrasée.

Le motif s'ajoute au précédent sans l'effacer : le registre ne réécrit pas son passé. La
trace nomme la référence retirée — sans quoi la correction serait indéchiffrable.

Deux refus tiennent à ce qui fonde la ligne. Une référence **venue de la réponse de la
DGI** ne se retire pas : elle fait foi, et seule une réconciliation manuelle, qui repose sur
la lecture d'un humain, peut être fautive. Et une référence ne se **remplace** jamais par
une autre — la ligne désignerait alors une autre facture chez la DGI. Une référence erronée
se retire ; elle ne se substitue pas.

### Savoir où en est une pièce

```powershell
dotnet run --project src\SageFne.Reader -- statut 1052
```

Elle ne contacte rien et n'écrit rien : deux `SELECT` sur Sage et une lecture du registre.
Elle ne lit pas non plus la configuration d'accès, donc **la clé d'API ne peut pas y
apparaître**.

Elle montre Sage d'un côté — identité, `DO_Type`, date, client, total TTC, empreinte
courante — et le registre de l'autre : état, référence FNE, jeton du QR code, horodatage,
identité inscrite, empreinte du corps envoyé. Puis elle compare les deux empreintes, ce qui
sépare « certifiée et inchangée » de « certifiée puis modifiée dans Sage », et conclut par
ce que cet état autorise, avec la commande à taper ensuite.

C'est la commande à lancer après un envoi pour vérifier ce qui a été retenu, et avant un
envoi pour savoir si la pièce peut partir.

### Sortir une pièce du suspens

Seul le portail de la DGI dit si la facture y est arrivée. `debloquer` n'appelle aucune
API : elle inscrit au registre ce que l'exploitant y a lu, et exige qu'il le dise.

```powershell
# le portail ne connaît pas la pièce : elle redevient à certifier
dotnet run --project src\SageFne.Reader -- debloquer 1052 --non-certifiee --confirmer

# le portail la porte sous cette référence : elle est classée certifiée
dotnet run --project src\SageFne.Reader -- debloquer 1052 --reference 2304903U26000000930 --confirmer
```

Sans `--non-certifiee` ni `--reference`, la commande refuse : ce choix ne se devine pas.
Avec les deux, elle refuse aussi. Une pièce déjà `Certified` n'est jamais réécrite — la
correction d'une certification erronée passe par un avoir. La décision et sa date restent
au registre à côté de la réponse d'origine : rien n'est effacé.

### La configuration, hors du dépôt

```powershell
cd src\SageFne.Reader
dotnet user-secrets set "Fne:BaseUrl" "https://…test…/"
dotnet user-secrets set "Fne:ApiKey"  "…"
```

`SignPath`, `AuthenticationHeader` et `AuthenticationScheme` sont paramétrables dans
`appsettings.json` : la documentation de la DGI fait foi, pas ce que le code suppose. La
clé n'apparaît jamais en clair — elle est réduite à ses quatre premiers et quatre derniers
caractères partout où elle s'affiche.

```bash
dotnet run --project src/SageFne.Reader -- fne-check
```

vérifie que tout est en place — environnement, adresse, présence de la clé, en-tête
d'authentification — **sans appeler quoi que ce soit**. Elle ne contacte aucun service.

### Le garde-fou d'environnement

`Fne:Environment` vaut **`Test` par défaut**, et c'est voulu : un défaut de production
ferait certifier pour de vrai une configuration oubliée.

En `Test`, **une seule adresse est admise** — celle que publie la DGI :

```
http://54.247.95.108/ws
```

C'est du HTTP clair, sur une IP nue. Ce n'est pas défendable en général : **la clé d'API y
voyage en clair**, lisible de tout équipement traversé. D'où une **exception nominative
plutôt qu'une règle** — HTTP n'est jamais autorisé en tant que tel, cette adresse-ci l'est.
Toute autre adresse est refusée, en HTTP comme en HTTPS.

> N'utilisez jamais une clé de production sur cette adresse, et tenez la clé de test pour
> exposée. `fne-check` le rappelle à chaque exécution.

`http://54.247.95.108` sans le `/ws` est **normalisé** vers l'adresse officielle : sans le
chemin, l'adresse de signature serait `…/external/invoices/sign` au lieu de
`…/ws/external/invoices/sign`, et l'échec serait incompréhensible. Les barres finales, la
casse et le port implicite sont également normalisés.

`Fne:TestAllowedUrls` permet d'en déclarer d'autres — un bouchon local, par exemple. La
liste configurée **remplace** le défaut, et l'ajout est alors un acte délibéré.

En `Production`, l'exception ne s'applique plus : HTTPS obligatoire, sans dérogation.

### Ce qui protège du doublon

L'ordre des opérations. Le registre est marqué **`Sending` avant l'appel**, pas après : si
la machine s'arrête entre les deux, la trace existe. Les six états vivent **uniquement dans
le registre du middleware** — Sage reste en lecture seule et n'a aucune zone pour eux.

| État | Ce qu'il dit |
| --- | --- |
| `Pending` | lue dans Sage, pas encore contrôlée |
| `Validating` | contrôles en cours |
| `Ready` | contrôlée et traduite, elle peut partir |
| `Sending` | **requête partie, issue inconnue** |
| `Certified` | certifiée, référence en main |
| `Error` | bloquée par un contrôle, ou refusée par la plateforme |

`Sending` est le plus important. Un délai dépassé ou une réponse acceptée dont aucune
référence n'est lisible **y laisse la pièce** : la DGI l'a peut-être enregistrée, et un
renvoi créerait un doublon irrattrapable. Elle ne repart jamais automatiquement — il faut
vérifier sur le portail. Un refus franc (4xx), lui, redevient une `Error` que l'on peut
corriger et renvoyer.

La réponse brute est toujours conservée au registre, y compris en échec : le format exact
n'étant pas connu d'avance, c'est elle qui permettra de corriger la lecture des champs.
La référence est cherchée sous plusieurs noms plausibles, à la racine et un niveau plus bas.

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

## L'aperçu d'une pièce

```bash
dotnet run --project src/SageFne.Reader -- apercu 1052     # ou « detail », c'est la même
```

**Aucune API n'est contactée** : la commande lit Sage en `SELECT`, construit le corps FNE
et l'affiche. Elle ne connaît même pas l'adresse de la plateforme.

Un tableau donne **chaque champ FNE avec son origine** — de `F_COMPTET.CT_Identifiant` pour
le NCC à « figé » pour `invoiceType`. C'est le seul moyen de vérifier qu'aucune valeur n'a
été inventée. Ce que Sage ne porte pas reste vide et se signale : `CLIENT_SANS_EMAIL`,
`CLIENT_SANS_TELEPHONE`, `PAYMENT_METHOD_SUPPOSE`.

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
│   │                                    InvoiceBatch, CandidatFne, CommandLine
│   ├── Certification/                   ICertificationLedger, JsonCertificationLedger,
│   │                                    CertifiedInvoice, InvoiceFingerprint
│   ├── Configuration/                   FneOptions, ZeroVatOptions, FneApiOptions,
│   │                                    ServicesMiddleware (le câblage)
│   ├── Fne/                             IFneClient, HttpFneClient, InvoiceSender,
│   │                                    EtatFne
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
│   │                                    RegimeTvaZero, IZeroVatPolicy,
│   │                                    ConfiguredZeroVatPolicy, TaxCatalogue
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
