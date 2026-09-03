# Avant d'installer chez un client

Cette fiche se remplit **avant** d'aller sur le poste. Ce qui suit n'est pas
une précaution de principe : chaque ligne vient d'un incident réel, sur ce
projet, avec de vraies factures parties à la DGI.

L'installation elle-même prend cinq minutes. Ce qui la précède en prend
davantage, et c'est là que se joue la réussite du déploiement.

---

## 1. Ce que le client doit obtenir de la DGI

**Chaque client a son propre accès FNE.** Rien ne se partage entre deux
entreprises : ni la clé, ni le NCC, ni le point de vente.

| À obtenir | Pourquoi c'est bloquant |
|---|---|
| Le **NCC** de l'entreprise | il identifie le contribuable dans chaque référence certifiée |
| La **clé d'API FNE** | sans elle, rien ne part ; elle est propre au contribuable |
| Le **point de vente** déclaré | doit correspondre **exactement** à ce qui est enregistré chez la DGI |
| L'**établissement** déclaré | idem |
| Le **crédit** de certification | chaque facture certifiée en consomme ; à zéro, tout s'arrête |

> **Le point de vente et l'établissement sont le piège n° 1.** Aucun contrôle
> ne peut les vérifier avant l'envoi : ils ne viennent pas de Sage. Une facture
> parfaite part, et la DGI répond « Establishment is invalid ». C'est arrivé
> sur quatre factures d'affilée avant qu'on comprenne. Faites-les confirmer
> **par écrit**, orthographe comprise.

### Essai puis production, jamais l'inverse

Ce sont deux plateformes distinctes, avec **deux clés différentes**. Installez
toujours en essai d'abord, certifiez deux ou trois factures réelles, vérifiez
sur le portail, puis basculez.

L'installeur vise l'essai par défaut. La production se demande explicitement,
avec `--production`.

---

## 2. Ce que le client doit préparer dans Sage

C'est la partie la plus longue, et celle qu'on découvre trop tard si on ne la
regarde pas. **Faites tourner le diagnostic sur une copie du dossier du client
avant de promettre une date.**

Sur votre poste, connecté à sa base :

```powershell
dotnet run --project src/SageFne.Reader -- ncc
dotnet run --project src/SageFne.Reader -- audit-tva-zero
dotnet run --project src/SageFne.Reader -- domaines
```

Ce que ces trois commandes révèlent, et qui doit être corrigé **dans Sage** :

| Ce qui manque | Conséquence si on ne le corrige pas |
|---|---|
| **NCC du client** absent | facture bloquée en B2B, elle ne part pas |
| **Téléphone du client** absent | la DGI le marque obligatoire |
| **Code de TVA** absent sur une ligne | `TVA_ABSENTE`, la pièce est bloquée |
| **Référence ou désignation d'article** vide | la DGI exige une description |
| **Quantité nulle ou négative** | refus de la plateforme |
| **Même NCC sur deux comptes** | les ventes de l'un partiraient sous le nom de l'autre |

> Sur le premier dossier réel, **995 factures sur 1 004 étaient incomplètes**,
> réparties sur 79 comptes. Ce n'est pas une exception, c'est l'état normal
> d'un dossier qui n'a jamais eu à porter ces champs.

`ncc` produit la liste des appels à passer, classée deux fois : par montant et
par nombre de factures. Ce ne sont pas les mêmes comptes. Donnez cette liste au
client — c'est son travail, pas le vôtre, et il prendra des semaines.

### La TVA à 0 %, à trancher par écrit

Une ligne à 0 % peut relever de `TVAC` (exonération conventionnelle) ou de
`TVAD` (exonération légale TEE/RME). **Sage ne les distingue pas**, et le
régime dépend de l'acheteur, pas du produit.

Le middleware refuse de deviner : il bloque la pièce plutôt que de déclarer un
régime fiscal au hasard. Il faut une **confirmation écrite** du client ou de la
DGI, saisie ensuite comme règle. Ne déduisez jamais ce régime de l'historique :
une suite de ventes à 0 % peut être une erreur de saisie répétée depuis deux
ans.

---

## 3. Le compte SQL, en lecture seule

Le middleware n'écrit **jamais** dans Sage. Le compte doit le garantir même si
le code se trompait. Ne réutilisez pas un compte applicatif existant.

```sql
-- Sur l'instance SQL Server qui porte le dossier Sage.
create login lecteur_fne with password = 'CHOISIR_UN_MOT_DE_PASSE_SOLIDE';

use [NOM_DE_LA_BASE_SAGE];
create user lecteur_fne for login lecteur_fne;
alter role db_datareader add member lecteur_fne;
```

`db_datareader` et rien d'autre : ni `db_datawriter`, ni `db_owner`.

Notez le mot de passe dans le gestionnaire de secrets du client, pas dans un
courriel. L'installeur le demande sans l'afficher.

---

## 4. Le poste qui portera l'agent

### Un seul agent par dossier Sage

**C'est la règle la plus importante de ce document.** Deux agents sur la même
base tiennent deux registres qui s'ignorent : chacun lit les mêmes factures,
chacun croit qu'elles ne sont pas parties, et **chacun les envoie**.

Ce n'est pas théorique. Sur le dossier d'essai, une facture a été certifiée
deux fois pour cette raison exacte — deux mémoires qui se croyaient toutes deux
vraies. Il a fallu émettre un avoir.

Les autres postes du client **n'ont pas besoin d'agent** : ils ouvrent l'écran
distant dans un navigateur. Dix comptables, un seul agent.

> Rien ne l'empêche encore techniquement. C'est une consigne d'installation,
> pas une garantie du produit, et c'est le prochain chantier.

### Le bon poste

| Critère | Pourquoi |
|---|---|
| **Allumé en permanence** | un service arrêté ne certifie rien |
| **Windows 64 bits**, 10 / 11 ou Server 2016+ | l'agent est publié pour win-x64 |
| **Droits administrateur** pour l'installation | service Windows et variables machine |
| **250 Mo** de disque libre | l'agent, ses journaux, le registre |
| Accès réseau au **serveur SQL** | port 1433 en général |
| Accès **sortant à la plateforme FNE** | voir ci-dessous |

Le serveur qui héberge Sage est souvent le meilleur choix : il est allumé, il
est sauvegardé, et il voit SQL Server sans traverser le réseau.

### Le pare-feu, à vérifier avant de vous déplacer

La plateforme d'essai de la DGI est en **HTTP clair** :

```
http://54.247.95.108/ws
```

Beaucoup de réseaux d'entreprise bloquent le port 80 sortant vers une adresse
IP brute, ou le font passer par un proxy. Testez-le depuis le poste visé :

```powershell
Test-NetConnection 54.247.95.108 -Port 80
```

Si ça ne répond pas, l'installation réussira et **rien ne partira jamais** —
l'agent dira « plateforme injoignable » dans son journal, et c'est tout. Faites
ouvrir le flux avant.

> Cette adresse étant en clair, n'y mettez **jamais** une clé de production.
> Le middleware l'interdit d'ailleurs : en production, il refuse toute adresse
> qui n'est pas en HTTPS.

### L'antivirus et SmartScreen

`SageFneSetup.exe` n'est **pas signé**. Windows affichera « Windows a protégé
votre ordinateur », et certains antivirus mettront le fichier en quarantaine :
un exécutable de 73 Mo qui installe un service coche toutes les cases.

Prévenez le client, et prévoyez l'exception. Pour une diffusion vraiment
professionnelle, il faudra **un certificat de signature de code** — c'est un
achat annuel, et c'est ce qui fait disparaître l'avertissement.

---

## 5. La sauvegarde, à décider avant d'installer

```
C:\ProgramData\SageFne\certifications.json
```

**C'est la seule mémoire des certifications.** Sage n'en porte aucune trace.
Le perdre ferait repartir à la DGI des factures déjà certifiées, et une facture
certifiée deux fois ne se reprend que par un avoir.

Ce fichier doit entrer dans le plan de sauvegarde du client, au même titre que
sa base comptable. Faites-le acter avant l'installation, pas après.

---

## 6. L'écran distant, si le client le veut

Facultatif. Sans lui, tout fonctionne — le tableau de bord local suffit.

| À préparer | |
|---|---|
| Un projet Supabase | un seul suffit pour **tous** vos clients |
| Une ligne `tenants` | l'entreprise |
| Une ligne `dossiers` | son dossier Sage, avec son environnement FNE |
| Un compte par utilisateur | dans Authentication |
| Une ligne `membres` par utilisateur | `proprietaire`, `exploitant` ou lecteur |

La séparation entre clients est appliquée **par la base**, pas par le code :
un membre d'une entreprise ne peut pas lire les factures d'une autre, même si
l'application se trompait.

La marche à suivre complète est dans `web/README.md`.

---

## Fiche à remplir, un client par colonne

```
Client                        ______________________________
NCC                           ______________________________
Clé d'API FNE                 reçue le ____________  (essai / production)
Point de vente (exact)        ______________________________
Établissement (exact)         ______________________________
Crédit de certification       ______________________________

Serveur SQL                   ______________________________
Base du dossier Sage          ______________________________
Compte lecture seule créé     oui / non    le ____________

Poste retenu pour l'agent     ______________________________
Allumé en permanence          oui / non
Port 80 vers 54.247.95.108    testé le ____________  OK / bloqué
Exception antivirus posée     oui / non

Diagnostic Sage passé         le ____________
  factures incomplètes        ______ sur ______
  comptes sans NCC            ______
  TVA à 0 % à trancher        oui / non
Corrections rendues au client le ____________

Registre au plan de sauvegarde   oui / non    validé par ____________

Écran distant demandé         oui / non
```

---

## Ce que le produit ne fait pas encore

Dit ici plutôt que découvert chez un client :

- **Rien n'empêche deux agents** sur le même dossier Sage. Consigne, pas
  garantie.
- **Aucune supervision centrale.** Le battement de cœur de l'agent va dans son
  journal local : avec vingt clients, vous ne savez pas lequel est tombé.
- **L'exécutable n'est pas signé.** SmartScreen avertira à chaque installation.
- **Le registre ne se restaure pas** depuis la base d'audit, qui en garde
  pourtant une copie.
- **Le paramétrage vit à deux endroits** — `appsettings.json` du poste et la
  table `dossiers`. Seul le fichier est lu aujourd'hui.
