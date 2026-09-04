# Base d'audit du SaaS

Ce que Sage ne peut pas porter : l'état de certification d'une pièce, sa
référence DGI, et le paramétrage fiscal que le dossier ne permet pas de déduire.

**Aucune donnée Sage n'est répliquée ici.** La base commerciale reste en lecture
seule, et ce schéma ne stocke d'elle que le numéro de pièce et les totaux — de
quoi retrouver une facture, pas de quoi la reconstituer.

**La clé d'API FNE n'a volontairement aucune colonne.** Une clé en base est une
clé qui fuite : elle vit dans un gestionnaire de secrets, hors de Postgres.

## Appliquer

```bash
supabase link --project-ref <votre-ref>
supabase db push
```

Sur une base locale, pour vérifier sans projet distant :

```bash
supabase start
./supabase/tests/executer.sh "postgres://postgres:postgres@localhost:54322/postgres"
```

La CI le fait à chaque push, contre un PostgreSQL 16 vierge.

## Ce que la base refuse, quoi qu'en dise l'applicatif

Une facture certifiée deux fois ne se rattrape que par un avoir. Les garanties
qui l'empêchent ne peuvent pas dépendre du seul code appelant.

| Garantie | Comment |
| --- | --- |
| Une pièce ne peut être certifiée deux fois | `unique (dossier_id, identite)` |
| La comptabilisation ne crée pas de doublon | l'identité est `domaine/DO_DocType/DO_Piece`, insensible au passage de `DO_Type` 6 à 7 |
| Une pièce certifiée le reste | `certified` est terminal, la référence ne se modifie plus |
| Un envoi en suspens ne repart pas seul | `sending` ne redevient jamais `ready` en silence |
| Certifiée implique une référence | contrainte `check` |
| L'historique ne se réécrit pas | déclencheur en ajout seul |
| Un client ne voit pas les factures d'un autre | RLS sur les sept tables, via `membres` |

## `sending` — le premier écran du matin

```sql
select * from certifications_en_suspens;
```

Chaque ligne est une facture partie dont on ignore l'issue : la DGI l'a
peut-être enregistrée. **Vérifiez sur le portail avant tout renvoi**, puis :

```sql
-- Le portail ne la connaît pas : elle repart au circuit normal.
select debloquer_envoi('<id>', 'Portail DGI : aucune trace au 31/08.');

-- Le portail la connaît : on inscrit sa référence sans la renvoyer.
select debloquer_envoi('<id>', 'Trouvée sur le portail.', true, '2304903U26000001052');
```

Le motif est obligatoire et tracé : c'est une décision humaine, elle laisse une
trace nominative.

## Les tables

| Table | Ce qu'elle porte |
| --- | --- |
| `tenants`, `membres` | qui accède à quoi — toutes les politiques RLS en dépendent |
| `dossiers` | un dossier Sage : environnement FNE, point de vente, établissement |
| `certifications` | le registre : une ligne par pièce, son état, sa référence |
| `certification_evenements` | chaque changement d'état, en ajout seul |
| `regles_tva_zero` | TVAC ou TVAD, par article, famille, client ou dossier |
| `mappings_prelevements` | quel code Sage part en `customTaxes`, sous quel nom |
