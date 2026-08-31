-- ---------------------------------------------------------------------------
-- Ce qu'une certification doit porter elle-même
--
-- Trois manques sont apparus le jour où la trace d'une facture certifiée a
-- disparu du registre fichier.
--
-- 1. L'environnement vivait sur le dossier, pas sur la certification. Basculer
--    un dossier de test en production aurait requalifié après coup toutes ses
--    certifications d'essai en certifications réelles. L'environnement dans
--    lequel une facture a été certifiée est un fait acquis : il se fige sur la
--    ligne, et un déclencheur interdit de le changer.
--
-- 2. L'unicité portait sur (dossier, identité). La même facture peut pourtant
--    être certifiée légitimement en test puis en production — ce sont deux
--    plateformes distinctes. L'unicité descend donc au triplet.
--
-- 3. Rien ne disait d'où venait une ligne. Une certification observée par le
--    middleware et une certification recopiée à la main depuis le portail
--    n'ont pas la même valeur probante : la seconde repose sur la lecture d'un
--    humain. Le distinguer est indispensable à un audit.
-- ---------------------------------------------------------------------------

-- --- D'où vient ce qu'on sait -----------------------------------------------

create type source_certification as enum (
  -- Le middleware a envoyé la facture et lu la réponse de la DGI.
  'middleware',
  -- Un humain a relevé la référence sur le portail ou le PDF, et l'a inscrite.
  -- L'empreinte est alors celle du document au moment du rattrapage, et non
  -- celle du corps réellement envoyé, qui est perdu.
  'reconciliation_manuelle',
  -- Reprise d'un registre antérieur ou d'un autre outil.
  'import'
);

comment on type source_certification is
  'Ce qui fonde la ligne. Une réconciliation manuelle repose sur la lecture d''un humain, pas sur une réponse de la DGI.';

-- --- Les colonnes manquantes ------------------------------------------------

alter table certifications
  add column environnement          fne_environnement,
  add column base_sage              text not null default '',
  add column tentatives             integer not null default 0 check (tentatives >= 0),
  add column dernier_code_http      smallint check (dernier_code_http between 100 and 599),
  add column source                 source_certification not null default 'middleware',
  add column reconciliee_le         timestamptz,
  add column reconciliee_par        uuid;

-- Les lignes déjà présentes héritent de l'environnement de leur dossier : c'est
-- l'information dont elles disposaient jusqu'ici.
update certifications c
   set environnement = d.environnement,
       base_sage     = d.base_sage
  from dossiers d
 where d.id = c.dossier_id;

-- Non renseigné à l'insertion, l'environnement est celui du dossier : c'est ce
-- que le middleware vise ce jour-là. Le figer sur la ligne le protège d'une
-- bascule ultérieure du dossier, sans obliger chaque appelant à le répéter.
create or replace function environnement_du_dossier() returns trigger
language plpgsql as $$
begin
  if new.environnement is null then
    select d.environnement into new.environnement from dossiers d where d.id = new.dossier_id;
  end if;
  if new.base_sage = '' then
    select d.base_sage into new.base_sage from dossiers d where d.id = new.dossier_id;
  end if;
  return new;
end;
$$;

create trigger certifications_environnement_par_defaut
  before insert on certifications
  for each row execute function environnement_du_dossier();

alter table certifications
  alter column environnement set not null;

comment on column certifications.environnement is
  'Figé à l''insertion. Une certification d''essai ne doit jamais devenir une certification réelle parce que le dossier a basculé.';
comment on column certifications.base_sage is
  'Nom de la base Sage d''origine, recopié pour que la ligne reste lisible seule.';
comment on column certifications.tentatives is
  'Nombre d''envois partis. Deux tentatives pour une certification signalent un premier envoi dont l''issue s''est perdue.';
comment on column certifications.dernier_code_http is
  'Code de la dernière réponse. Un 5xx laisse l''issue inconnue ; un 4xx est un refus net.';
comment on column certifications.source is
  'middleware : réponse de la DGI observée. reconciliation_manuelle : référence relevée par un humain.';

-- --- L'unicité, au bon niveau -----------------------------------------------

alter table certifications
  drop constraint certifications_dossier_id_identite_key;

alter table certifications
  add constraint certifications_uniques_par_environnement
    unique (dossier_id, environnement, identite);

comment on constraint certifications_uniques_par_environnement on certifications is
  'La garantie anti-doublon. Le triplet, et non la paire : la même facture peut être certifiée en test puis en production.';

-- --- Ce que la base refuse --------------------------------------------------

-- L'environnement d'une certification est un fait acquis.
create or replace function figer_environnement_certification() returns trigger
language plpgsql as $$
begin
  if new.environnement is distinct from old.environnement then
    raise exception
      'L''environnement d''une certification ne se change pas (% -> %). La pièce % a été certifiée sur cette plateforme-là.',
      old.environnement, new.environnement, old.piece
      using errcode = 'check_violation';
  end if;
  return new;
end;
$$;

create trigger certifications_environnement_fige
  before update on certifications
  for each row execute function figer_environnement_certification();

-- Une réconciliation manuelle dit quand elle a eu lieu : sans cette date, rien
-- ne distingue à l'audit une référence constatée d'une référence reçue.
alter table certifications
  add constraint certification_reconciliee_est_datee
    check (source <> 'reconciliation_manuelle'
           or (reconciliee_le is not null and etat = 'certified'));

comment on constraint certification_reconciliee_est_datee on certifications is
  'Une réconciliation manuelle porte sa date et conclut une certification : c''est son seul objet.';

-- Le compteur de tentatives ne redescend pas : il compte des envois partis, et
-- un envoi parti ne se retire pas du passé.
create or replace function tentatives_ne_reculent_pas() returns trigger
language plpgsql as $$
begin
  if new.tentatives < old.tentatives then
    raise exception
      'Le nombre de tentatives ne peut pas diminuer (% -> %) : un envoi parti reste parti.',
      old.tentatives, new.tentatives
      using errcode = 'check_violation';
  end if;
  return new;
end;
$$;

create trigger certifications_tentatives_croissantes
  before update on certifications
  for each row execute function tentatives_ne_reculent_pas();

-- --- Retrouver une certification sans connaître son dossier -----------------

-- Le cas d'usage du rattrapage : « cette référence FNE, à quelle pièce
-- correspond-elle ? ». Partiel, parce qu'une ligne sans référence n'intéresse
-- pas cette recherche.
create index certifications_par_reference on certifications (reference_fne)
  where length(btrim(reference_fne)) > 0;

-- Les lignes réconciliées à la main : ce qu'un auditeur voudra revoir en premier.
create index certifications_reconciliees on certifications (dossier_id, reconciliee_le)
  where source = 'reconciliation_manuelle';
