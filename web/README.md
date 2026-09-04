# L'écran distant

Une page, un fichier, aucune chaîne de compilation. La même approche que le
tableau de bord local de l'agent : ce qui se lit se corrige.

## Ce qu'elle fait, et ce qu'elle ne fait pas

Elle **lit** les certifications de votre organisation et **demande** qu'une
pièce soit certifiée. Elle ne certifie rien elle-même, et ne le pourra jamais :

- Sage est sur le poste, en lecture seule. Aucun serveur distant ne l'atteint.
- La clé d'API FNE est une variable machine du poste. Elle n'en sort pas.
- Le registre local est la seule mémoire anti-doublon.

Un clic ici écrit une ligne dans `demandes_certification`. L'agent la relit au
tour suivant, **refait tous ses contrôles** par le même chemin que son bouton
local, et décide. Le verdict revient dans la même ligne.

C'est la seule architecture qui tienne. Une facture de ce dossier a déjà été
certifiée deux fois parce que deux mémoires se croyaient toutes deux vraies :
le cloud ne sera pas la troisième.

## Comment lire les commandes qui suivent

Elles sont écrites pour **PowerShell**, et se collent telles quelles une fois
les valeurs remplacées. Deux pièges de ce shell, tous deux rencontrés :

- `&&` n'y est pas un séparateur. Une commande par ligne.
- Les chevrons sont réservés. Aucune valeur à remplacer n'en porte ici : les
  emplacements sont écrits `A_COMPLETER`, qui ne s'exécute pas par accident et
  que le middleware refuse s'il vous échappe.

Un texte laissé à `A_COMPLETER` n'allume rien et le dit. Un texte recopié entre
chevrons, lui, s'installe en silence : c'est arrivé six fois sur ce projet, une
fois jusqu'à faire refuser toutes les factures par la DGI.

## Mise en route

### 1. La base

Remplacez `A_COMPLETER` par la référence de votre projet Supabase, puis :

```powershell
supabase link --project-ref A_COMPLETER
supabase db push
```

### 2. Votre organisation, votre dossier, votre compte

Créez d'abord votre compte dans l'onglet **Authentication** du tableau de bord
Supabase. Puis, dans l'éditeur SQL, une requête à la fois — chacune vous rend
l'identifiant dont la suivante a besoin :

```sql
insert into tenants (nom) values ('FISH AFRIC') returning id;
```

```sql
-- Collez l'id rendu ci-dessus a la place de TENANT_ICI.
insert into dossiers (tenant_id, code, base_sage, environnement)
values ('TENANT_ICI', 'HT', 'HT', 'test') returning id;
```

```sql
-- TENANT_ICI : le meme qu'au-dessus.
-- USER_ICI   : votre identifiant, colonne « UID » dans Authentication.
insert into membres (tenant_id, user_id, role)
values ('TENANT_ICI', 'USER_ICI', 'proprietaire');
```

Gardez l'`id` du **dossier** rendu par la deuxième requête : le poste en a
besoin à l'étape 4.

### 3. La page

Ouvrez `web/config.js` et remplacez les deux `A_COMPLETER` par l'URL du projet
et la clé **anon** — l'une et l'autre se lisent dans *Project Settings → API*.

La clé anon n'est pas un secret : elle est faite pour vivre dans un navigateur,
et c'est la RLS, en base, qui décide de ce que chaque compte voit.

Servez ensuite le dossier `web/`. N'importe quel hébergement de fichiers
statiques convient — ou, pour essayer sans rien installer :

```powershell
python -m http.server 8080 --directory web
```

### 4. Le poste

Une seule ligne, avec vos deux valeurs à la place des `A_COMPLETER` :

```powershell
powershell -ExecutionPolicy Bypass -File .\deploiement\installer-agent.ps1 -Preparer -SupabaseUrl "A_COMPLETER" -Dossier "A_COMPLETER"
```

`-ExecutionPolicy Bypass` n'est pas décoratif : Windows refuse par défaut
d'exécuter un script `.ps1`, et le message d'erreur ne dit pas quoi faire.

Puis la clé de service, qui est un secret et ne va jamais dans un fichier du
dépôt. Remplacez `A_COMPLETER` par la valeur lue dans *Project Settings → API*,
ligne `service_role` :

```powershell
[Environment]::SetEnvironmentVariable('Saas__CleService', 'A_COMPLETER', 'Machine')
```

Redémarrez ensuite le service pour qu'il la voie :

```powershell
Restart-Service SageFneAgent
```

### 5. Vérifier

```powershell
powershell -ExecutionPolicy Bypass -File .\deploiement\installer-agent.ps1 -Preparer
```

La section « Base d'audit » de la sortie dit si le miroir est allumé, et ce qui
manque sinon.

## Si vous vous êtes trompé

Une variable machine se corrige en la réécrivant, et s'efface avec `$null` :

```powershell
[Environment]::SetEnvironmentVariable('Saas__CleService', $null, 'Machine')
```

Une valeur restée à `A_COMPLETER`, ou recopiée entre chevrons, laisse le miroir
éteint : le middleware la reconnaît comme un gabarit et refuse de s'en servir.
Rien ne part vers la base, et la certification continue normalement.

## Les rôles

| Rôle | Voit les factures | Peut demander une certification |
| --- | --- | --- |
| `proprietaire` | oui | oui |
| `exploitant` | oui | oui |
| tout autre | oui | non |

La RLS l'applique en base. Retirer le bouton de la page ne protégerait rien.
