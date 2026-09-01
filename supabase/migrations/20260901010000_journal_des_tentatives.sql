-- ---------------------------------------------------------------------------
-- Le journal des tentatives, en ajout seul
--
-- « certification_evenements » ne trace que les changements d'état. Or un
-- doublon naît de ce qui ne change pas d'état : deux POST partis, deux 500,
-- une pièce qui reste « sending ». Rien de cela n'était consigné.
--
-- Sur la pièce 1072, la trace était même reconstruite à neuf à chaque envoi :
-- au second, la base aurait affirmé « cette pièce n'est jamais partie ». Ce
-- journal-ci compte les départs, et un départ ne s'oublie pas.
-- ---------------------------------------------------------------------------

create type genre_tentative as enum (
  -- Un POST est parti. Son issue n'est pas encore connue.
  'envoi',
  -- La plateforme a répondu — ou n'a pas répondu.
  'reponse',
  -- Un opérateur a tranché, portail en main.
  'decision',
  -- Un événement saisi après coup, que le middleware n'a pas observé.
  -- Les envois antérieurs à ce journal sont dans ce cas : leur histoire n'a
  -- pas été perdue, elle n'a jamais été écrite. La reconstituer est légitime ;
  -- la confondre avec un fait observé ne l'est pas.
  'reconstitue'
);

create table certification_tentatives (
  id                bigint generated always as identity primary key,
  certification_id  uuid not null references certifications(id) on delete cascade,

  genre             genre_tentative not null,

  -- La date des faits, et non celle de la saisie : un événement reconstitué
  -- porte l'heure à laquelle il a eu lieu, pour se ranger à sa place.
  survenu_le        timestamptz not null default now(),

  code_http         smallint check (code_http between 100 and 599),
  detail            text not null default '',

  -- Qui a saisi, pour les entrées humaines. Null pour ce qu'observe le middleware.
  saisi_par         uuid,
  cree_le           timestamptz not null default now(),

  -- Un fait observé ne se date pas à la main : seule une reconstitution peut
  -- porter une date antérieure à son écriture. La borne vaut des deux côtés —
  -- n'en poser qu'une laissait passer l'antidate, c'est-à-dire précisément ce
  -- que cette contrainte doit empêcher.
  constraint tentative_observee_est_datee_du_present
    check (genre = 'reconstitue'
           or survenu_le between cree_le - interval '1 minute'
                             and cree_le + interval '1 minute')
);

comment on table certification_tentatives is
  'Ce qui est arrivé à une pièce, dans l''ordre. En ajout seul : un envoi parti reste parti.';
comment on column certification_tentatives.genre is
  'reconstitue marque un fait saisi après coup, jamais observé par le middleware.';
comment on column certification_tentatives.survenu_le is
  'La date des faits. Pour une reconstitution, elle précède la date d''écriture — c''est tout son objet.';

create index tentatives_par_certification
  on certification_tentatives (certification_id, survenu_le);

-- Les envois partis, ce qu'il faut compter avant d'en lancer un de plus.
create index tentatives_envois
  on certification_tentatives (certification_id)
  where genre in ('envoi', 'reconstitue');

-- --- Rien ne s'y réécrit ni ne s'en retire ----------------------------------

create or replace function tentatives_en_ajout_seul() returns trigger
language plpgsql as $$
begin
  raise exception
    'certification_tentatives est en ajout seul : ni modification ni suppression.'
    using hint = 'Une tentative erronée se corrige par une nouvelle entrée qui la commente, jamais en effaçant la première.';
end;
$$;

create trigger tentatives_immuables
  before update or delete on certification_tentatives
  for each row execute function tentatives_en_ajout_seul();

-- --- Compter les envois d'une pièce -----------------------------------------

create or replace function envois_partis(la_certification uuid)
returns integer language sql stable as $$
  select count(*)::integer
    from certification_tentatives
   where certification_id = la_certification
     and (genre = 'envoi'
          or (genre = 'reconstitue' and detail ilike '%post%'));
$$;

comment on function envois_partis is
  'Combien de POST sont partis, reconstitutions comprises. Un second envoi doit savoir qu''il est le second.';

-- --- Les pièces parties plus d'une fois -------------------------------------

create or replace view certifications_multi_envois as
  select c.id, c.dossier_id, c.environnement, c.identite, c.piece, c.etat,
         c.reference_fne, envois_partis(c.id) as envois
    from certifications c
   where envois_partis(c.id) > 1;

comment on view certifications_multi_envois is
  'Les pièces pour lesquelles plusieurs POST sont partis : chacune est un doublon possible chez la DGI.';

-- --- Accès ------------------------------------------------------------------

alter table certification_tentatives enable row level security;

create policy tentatives_lecture on certification_tentatives
  for select using (
    exists (
      select 1 from certifications c
        join dossiers d on d.id = c.dossier_id
        join membres m on m.tenant_id = d.tenant_id
       where c.id = certification_tentatives.certification_id
         and m.user_id = auth.uid()
    )
  );

create policy tentatives_ajout on certification_tentatives
  for insert with check (
    exists (
      select 1 from certifications c
        join dossiers d on d.id = c.dossier_id
        join membres m on m.tenant_id = d.tenant_id
       where c.id = certification_tentatives.certification_id
         and m.user_id = auth.uid()
         and m.role in ('proprietaire', 'exploitant')
    )
  );

-- Aucune politique d'update ni de delete : le déclencheur les refuse déjà, et
-- leur absence le dit une seconde fois.
