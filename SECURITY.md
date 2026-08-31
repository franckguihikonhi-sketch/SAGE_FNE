# Sécurité

Ce middleware touche à deux choses sensibles : la comptabilité d'une entreprise,
et une clé qui permet de certifier des factures auprès de l'administration
fiscale. Voici ce que le projet tient, et ce qu'il attend de vous.

## Ce que le code garantit

**La base Sage est en lecture seule.** Toutes les requêtes sont des `SELECT`
paramétrés, filtrés par `ReadOnlyGuard` avant exécution. Le code n'appelle ni
`ExecuteNonQuery`, ni `SqlBulkCopy`, ni transaction. Les seuls identifiants
écrits dans une requête — les noms de table des commandes de diagnostic —
passent par `IdentifiantSql` puis sont vérifiés au catalogue.

**Aucun envoi n'est automatique.** `envoyer` simule par défaut ; seul
`--confirmer` déclenche un appel. Une certification ne s'annule pas.

**Aucun secret dans le dépôt.** `appsettings.json` ne porte que des gabarits.
La chaîne SQL et la clé FNE vivent dans `dotnet user-secrets`. La CI échoue si
une valeur en dur apparaît.

**La clé ne s'affiche jamais en clair**, ni en console, ni dans les journaux :
quatre caractères à chaque bout, le reste masqué.

## Ce qui vous revient

**Le compte SQL doit être `db_datareader`, rien de plus.** C'est la seule
garantie qui ne dépende pas de ce code.

**La plateforme d'essai de la DGI est en HTTP clair** — `http://54.247.95.108/ws`.
La clé y voyage lisible de tout équipement traversé. N'y utilisez jamais une clé
de production, et tenez la clé de test pour exposée.

**Ne collez jamais un secret dans un ticket, une capture ou un message.** Si
c'est arrivé, changez-le : `ALTER LOGIN … WITH PASSWORD` côté SQL, une nouvelle
clé côté DGI.

## Signaler une faille

N'ouvrez pas de ticket public. Écrivez au responsable du dépôt en décrivant le
problème et comment le reproduire.
