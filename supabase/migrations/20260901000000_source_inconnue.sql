-- ---------------------------------------------------------------------------
-- Une valeur par défaut ne doit jamais être une affirmation
--
-- « middleware » était la première valeur de source_certification, et le défaut
-- de la colonne. Une ligne dont personne n'avait renseigné la source se
-- déclarait donc « la DGI l'a dit » — l'affirmation la plus forte que le champ
-- sache porter, tirée d'une absence d'information.
--
-- Le middleware a fait la même faute, avec les mêmes conséquences : une
-- réconciliation manuelle réelle s'est retrouvée classée réponse de plateforme,
-- et devenue incorrigible — les corrections étant réservées aux déclarations
-- humaines, qui seules peuvent être fautives.
--
-- La valeur par défaut devient l'aveu d'une ignorance, ce qu'elle est.
-- ---------------------------------------------------------------------------

alter type source_certification add value if not exists 'inconnue' before 'middleware';
