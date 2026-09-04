-- ---------------------------------------------------------------------------
-- Ce qu'un exploitant regarde le matin.
-- ---------------------------------------------------------------------------

-- Les envois dont l'issue est inconnue. À traiter avant toute autre chose :
-- chacun est une facture peut-être certifiée à la DGI et pas chez nous.
create view certifications_en_suspens as
select c.id,
       c.dossier_id,
       d.code as dossier,
       c.piece,
       c.identite,
       c.tiers,
       c.total_ttc,
       c.envoyee_le,
       now() - c.envoyee_le as depuis,
       c.erreur
  from certifications c
  join dossiers d on d.id = c.dossier_id
 where c.etat = 'sending'
 order by c.envoyee_le;

comment on view certifications_en_suspens is
  'Envois sans issue connue. Vérifier sur le portail DGI, puis debloquer_envoi().';

create view certifications_resume as
select d.id as dossier_id,
       d.code as dossier,
       c.etat,
       count(*) as pieces,
       sum(c.total_ttc) as total_ttc,
       min(c.date_piece) as premiere,
       max(c.date_piece) as derniere
  from dossiers d
  left join certifications c on c.dossier_id = d.id
 group by d.id, d.code, c.etat;

-- Les vues héritent du RLS des tables sous-jacentes.
alter view certifications_en_suspens set (security_invoker = on);
alter view certifications_resume set (security_invoker = on);
