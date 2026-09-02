-- ---------------------------------------------------------------------------
-- « transmise » : arrivée au portail, en attente du clic
--
-- Découvert en exploitation, sur la pièce 1221. Le POST ne certifie pas : il
-- dépose la facture au portail de la DGI, et c'est un clic sur ce portail qui
-- la certifie et lui donne sa référence.
--
-- Entre les deux, la pièce n'était descriptible par aucun état. « sending »
-- veut dire « issue inconnue » — ici l'issue est connue, la facture est
-- arrivée. « certified » veut dire « la DGI l'a certifiée » — personne ne l'a
-- encore fait. Faute d'un mot juste, une facture déposée serait restée
-- indéfiniment en suspens, et un avertissement qui crie au loup à chaque
-- passage finit par ne plus être lu.
--
-- Ce qui compte : cet état bloque le renvoi aussi fermement que « sending ».
-- La facture est déjà là-bas ; l'y renvoyer l'y mettrait deux fois.
-- ---------------------------------------------------------------------------

alter type fne_etat add value 'transmise';

comment on type fne_etat is
  'Les sept états de la chaîne. « sending » : issue inconnue. « transmise » : arrivée au portail, en attente du clic. Ni l''un ni l''autre ne repart seul.';
