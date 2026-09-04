# Brancher le middleware sur la base Sage

À faire **sur un poste Windows qui voit le serveur SQL de Sage** — le poste Sage
lui-même, ou un serveur du même réseau. Pas depuis un autre réseau : SQL Server
n'est en général pas exposé à l'extérieur.

Comptez 15 minutes, dont 5 pour votre DBA.

---

## 1. Installer le SDK .NET 8

<https://dotnet.microsoft.com/download/dotnet/8.0> → **SDK x64** (pas seulement
le Runtime : `dotnet run` a besoin du SDK).

Vérification, dans un nouveau PowerShell :

```powershell
dotnet --version
```

Doit afficher `8.x.x`.

---

## 2. Récupérer le code

```powershell
cd C:\
git clone https://github.com/franckguihikonhi-sketch/SAGE_FNE.git
cd SAGE_FNE
git checkout claude/fne-sage-invoice-export-9fmmon
dotnet build
```

Sans Git installé : <https://git-scm.com/download/win>, ou téléchargez le ZIP de
la branche depuis GitHub.

---

## 3. Trouver le nom du serveur SQL

Trois façons, de la plus simple à la plus sûre :

| Méthode | Où |
| --- | --- |
| Dans Sage | `?` → `À propos de` → l'onglet qui cite le serveur de données |
| Services Windows | `services.msc` → une entrée `SQL Server (NOM_INSTANCE)` |
| En SQL | Sur le serveur : `SELECT @@SERVERNAME` |

Le nom prend souvent la forme `MONSERVEUR\SAGE100` ou `MONSERVEUR\SQLEXPRESS`.
L'instance après l'antislash compte : sans elle, la connexion échoue.

Notez aussi le **nom exact de la base** — nous partons sur `HT`, à confirmer.

---

## 4. Créer un compte SQL en lecture seule

**C'est la garantie principale.** Le code n'exécute que des `SELECT`, mais un
compte `db_datareader` rend une écriture *impossible* même en cas de bug : la
sécurité ne repose plus sur ma parole.

À faire exécuter **par votre DBA**, dans SQL Server Management Studio :

```sql
CREATE LOGIN lecteur_fne WITH PASSWORD = 'UnMotDePasseSolide!2026';
USE HT;
CREATE USER lecteur_fne FOR LOGIN lecteur_fne;
ALTER ROLE db_datareader ADD MEMBER lecteur_fne;
```

Ne lui donnez **ni** `db_datawriter`, **ni** `db_owner`, **ni** `sysadmin`.

Si le serveur n'accepte que l'authentification Windows, sautez cette étape et
voyez la variante au point 5.

---

## 5. Renseigner la connexion — hors du dépôt

Le fichier `appsettings.json` est suivi par Git : **n'y mettez jamais le mot de
passe**. Il va dans les secrets utilisateur, stockés dans votre profil Windows.

```powershell
cd src\SageFne.Reader
dotnet user-secrets set "ConnectionStrings:Sage" "Server=MONSERVEUR\SAGE100;Database=HT;User Id=lecteur_fne;Password=UnMotDePasseSolide!2026;TrustServerCertificate=True;"
dotnet user-secrets list
```

Avec l'authentification Windows à la place du compte SQL :

```powershell
dotnet user-secrets set "ConnectionStrings:Sage" "Server=MONSERVEUR\SAGE100;Database=HT;Integrated Security=True;TrustServerCertificate=True;"
```

Le secret atterrit dans
`%APPDATA%\Microsoft\UserSecrets\sagefne-reader-ht\secrets.json`, hors du dépôt.

---

## 6. Vérifier que ça parle à la base

```powershell
cd C:\SAGE_FNE
dotnet run --project src\SageFne.Reader -- doctypes
```

**Le signe que c'est branché**, en tête de sortie :

```
Source : base Sage (SQL Server), en lecture seule.
```

Si vous lisez à la place « Source : jeu d'essai hors base », la chaîne n'est pas
prise en compte — voyez le tableau plus bas.

---

## 7. Les trois commandes à me renvoyer

```powershell
dotnet run --project src\SageFne.Reader -- doctypes
dotnet run --project src\SageFne.Reader -- 1219
dotnet run --project src\SageFne.Reader -- --du 2025-12-01 --au 2025-12-31
```

Copiez-moi les sorties. Elles répondent aux trois questions encore ouvertes :
le `DO_Type` réel de vos factures, l'emplacement de la TVA sur vos lignes, et
la présence du NCC sur vos clients.

Aucune de ces commandes n'écrit quoi que ce soit, ni dans Sage, ni ailleurs.

---

## Si ça coince

| Message | Cause | Remède |
| --- | --- | --- |
| « Source : jeu d'essai hors base » | Le secret n'est pas lu, ou contient encore `SERVEUR_SQL` / `MOT_DE_PASSE` | `dotnet user-secrets list` depuis `src\SageFne.Reader` ; le programme rejette exprès les valeurs du gabarit |
| `A network-related or instance-specific error` | Nom d'instance faux, ou SQL Server n'écoute pas en TCP/IP | Vérifiez le nom au point 3 ; activez TCP/IP dans SQL Server Configuration Manager |
| `Login failed for user 'lecteur_fne'` | Mot de passe, ou login non créé sur la base `HT` | Rejouez le script du point 4 |
| `Cannot open database "HT"` | La base ne s'appelle pas `HT` | Listez-les : `SELECT name FROM sys.databases` |
| `A connection was successfully established… certificate` | Certificat SQL non approuvé | `TrustServerCertificate=True` doit être dans la chaîne |
| `Invalid object name 'F_DOCENTETE'` | Base atteinte, mais ce n'est pas le dossier commercial Sage | Vérifiez le nom de base au point 3 |
| `MSB1009: Project file does not exist` | Mauvais répertoire courant | Revenez à la racine `C:\SAGE_FNE` |

---

## Ce que le middleware fait, et ne fait pas

- Il ouvre une connexion, exécute des `SELECT` paramétrés, ferme.
- Chaque requête passe par un garde-fou qui refuse tout verbe d'écriture avant
  même l'envoi au serveur.
- Il n'appelle jamais `ExecuteNonQuery`, ni transaction, ni copie en masse.
- Il ne contacte aucune API : la DGI n'est pas branchée à ce stade.
- Le seul fichier qu'il peut écrire est un JSON d'export, et seulement si vous
  passez `--sortie`.
