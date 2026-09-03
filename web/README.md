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

## Mise en route

1. Créez le projet Supabase, puis appliquez les migrations :

   ```bash
   supabase link --project-ref <votre-ref>
   supabase db push
   ```

2. Créez votre organisation, votre dossier, et rattachez-vous :

   ```sql
   insert into tenants (nom) values ('FISH AFRIC') returning id;
   insert into dossiers (tenant_id, code, base_sage, environnement)
   values ('<tenant>', 'HT', 'HT', 'test') returning id;
   -- Puis, après avoir créé votre compte dans Authentication :
   insert into membres (tenant_id, user_id, role)
   values ('<tenant>', '<votre user_id>', 'proprietaire');
   ```

3. Renseignez `config.js` avec l'URL du projet et la clé **anon**.

4. Servez le dossier. N'importe quel hébergement de fichiers statiques convient
   — ou, pour essayer sans rien installer :

   ```bash
   python3 -m http.server 8080 --directory web
   ```

5. Sur le poste, allumez le miroir et les demandes :

   ```powershell
   .\deploiement\installer-agent.ps1 -Preparer -SupabaseUrl "https://xxxx.supabase.co" -Dossier "<uuid du dossier>"
   [Environment]::SetEnvironmentVariable('Saas__CleService', '<service_role>', 'Machine')
   ```

## Les rôles

| Rôle | Voit les factures | Peut demander une certification |
| --- | --- | --- |
| `proprietaire` | oui | oui |
| `exploitant` | oui | oui |
| tout autre | oui | non |

La RLS l'applique en base. Retirer le bouton de la page ne protégerait rien.
