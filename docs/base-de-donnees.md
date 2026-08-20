# Base de données Supabase

## Ce qu'elle apporte

La passerelle fonctionne aujourd'hui entièrement dans le navigateur, et cela reste vrai : **la
conversion ne remonte jamais dans le cloud.** La base sert à trois choses que le stockage local d'un
poste ne peut pas rendre.

**Partager le paramétrage.** La table de correspondance clients, les modes de règlement et les
réglages Sage vivent aujourd'hui dans le navigateur d'un seul poste. En base, ils suivent le
dossier : un deuxième comptable, un poste remplacé, et tout est déjà là.

**Empêcher les doublons.** Rien n'empêche aujourd'hui de réimporter deux fois le même lot dans Sage.
La base retient la référence FNE de chaque facture déjà convertie ; un index unique par dossier la
refuse une seconde fois.

**Garder une trace.** Qui a converti quel fichier, quand, combien de pièces, pour quels totaux.

## Ce qu'elle ne conserve pas

**Le détail des lignes d'articles n'est jamais stocké.** Seul l'entête des factures remonte
(référence FNE, date, client, totaux) — le strict nécessaire au contrôle des doublons. Le contenu
commercial de chaque ligne reste sur le poste.

## Tables

| Table | Rôle |
| --- | --- |
| `dossiers` | Une entreprise émettrice, identifiée par son NCC |
| `membres` | Qui accède à quel dossier, comme `gestionnaire` ou `operateur` |
| `parametres` | Format d'import, dépôt, souche, numérotation, compte par défaut |
| `comptes_tiers` | Correspondance client FNE → compte tiers Sage |
| `modes_reglement` | Mode de paiement FNE → code règlement Sage |
| `profils_import` | Format d'import du dossier, en JSON : adapter un dossier sans redéployer |
| `conversions` | Historique : fichier, totaux, anomalies, date d'import dans Sage |
| `factures` | Entête des factures converties, pour la détection des doublons |

Trois fonctions appelables depuis l'application :

- `creer_dossier(nom, ncc)` — crée le dossier, y inscrit son auteur comme gestionnaire et pose les
  paramètres par défaut, en une transaction ;
- `references_deja_importees(dossier, refs[])` — parmi les références d'un export, celles déjà
  converties, avec la date et le fichier d'origine ;
- `enregistrer_comptes_tiers(dossier, entrees)` — enregistre la table de correspondance en une fois.

Et une vue `tableau_de_bord` : par dossier, le nombre de comptes tiers, de conversions, de factures
et la date de la dernière conversion.

## Isolation

La clé anonyme de Supabase est publique — elle est embarquée dans la page. **C'est donc la RLS, et
elle seule, qui empêche une entreprise de lire le dossier d'une autre.** Chaque table a `row level
security` activée et n'ouvre que ce qui passe par la fonction `est_membre`.

Un point mérite d'être signalé, parce qu'il est contre-intuitif : `INSERT … RETURNING` exige aussi
la politique de **lecture**. À la création d'un dossier, l'adhésion de l'auteur n'existe pas encore,
si bien que la lecture lui serait refusée sur la ligne qu'il vient d'écrire. La politique de lecture
autorise donc explicitement le créateur (`cree_par = auth.uid()`), en plus des membres.

## Créer la base

Les migrations sont dans `supabase/migrations/`, dans l'ordre d'application.

**Depuis l'interface Supabase** — le plus simple pour démarrer :

1. Créer un projet sur [supabase.com](https://supabase.com) (région Europe, la plus proche
   d'Abidjan en latence).
2. Ouvrir *SQL Editor* et exécuter les trois fichiers de `supabase/migrations/`, dans l'ordre de
   leur nom.
3. Relever dans *Project Settings → API* l'URL du projet et la clé `anon`.

**Avec la CLI Supabase**, si le projet est déjà lié :

```bash
supabase link --project-ref <ref-du-projet>
supabase db push
```

## Vérifier les migrations

`npm run test:db` applique les migrations sur un PostgreSQL local et rejoue quinze contrôles :
isolation entre deux dossiers, refus d'écriture chez un tiers, unicité des NCC casse comprise,
unicité de la référence FNE par dossier, détection des doublons, attribution des conversions.

Supabase n'est pas nécessaire pour cela : `supabase/tests/stub-auth.sql` reproduit localement le
strict minimum du schéma `auth` (la table des utilisateurs, `auth.uid()`, les rôles `anon` et
`authenticated`). Ce fichier ne fait pas partie des migrations — Supabase fournit déjà tout cela.

Les contrôles s'exécutent sous le rôle `authenticated`, comme le fait l'API Supabase : la RLS
s'applique donc réellement, ce qui ne serait pas le cas sous le propriétaire des tables.
