## Ce que fait ce changement

<!-- Ce que ça change pour l'exploitant, pas la liste des fichiers touchés. -->

## Pourquoi

<!-- Le problème résolu. S'il vient d'une observation sur le dossier réel, dites laquelle. -->

## Vérifications

- [ ] `dotnet test` passe
- [ ] Les migrations Supabase s'appliquent sur une base vierge (`supabase/tests/executer.sh`)
- [ ] Aucun secret ajouté au dépôt — ni clé FNE, ni mot de passe SQL
- [ ] Aucune écriture dans Sage : uniquement des `SELECT`
- [ ] Aucun POST vers la DGI non demandé explicitement

## Ce que ça n'a pas été vérifié contre

<!-- Le dossier réel ? un envoi FNE ? Dites ce qui reste à confronter au terrain. -->
