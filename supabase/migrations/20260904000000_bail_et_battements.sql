-- ---------------------------------------------------------------------------
-- Une piece ne part qu'une fois, et on sait quels agents sont vivants.
--
-- L'invariant n'est pas « un seul agent » : deux agents peuvent parfaitement
-- traiter deux factures differentes du meme dossier, et rien ne s'y oppose.
-- Ce qui ne doit jamais arriver, c'est que la MEME piece parte deux fois.
--
-- Aujourd'hui la seule memoire anti-doublon est le registre fichier, local a
-- chaque poste. Deux postes, deux registres qui s'ignorent, et la meme facture
-- part deux fois — c'est arrive sur ce dossier, et il a fallu un avoir.
--
-- La reservation ci-dessous donne cette memoire en partage. C'est PostgreSQL
-- qui departage, par la contrainte d'unicite qui existe deja sur les
-- certifications, et non le code appelant.
-- ---------------------------------------------------------------------------

create or replace function reserver_piece(
  p_dossier       uuid,
  p_environnement fne_environnement,
  p_identite      text,
  p_piece         text,
  p_agent         text
) returns boolean
language plpgsql security definer set search_path = public as $$
declare
  v_reserve boolean;
begin
  -- Marquee « sending » AVANT l'appel a la DGI, exactement comme le registre
  -- local le fait. Si la reponse se perd, la trace existe des deux cotes.
  insert into certifications (dossier_id, environnement, identite, piece, etat, envoyee_le, erreur)
  values (p_dossier, p_environnement, p_identite, p_piece, 'sending', now(),
          format('reservee par %s', p_agent))
  on conflict (dossier_id, environnement, identite) do update
     set etat       = 'sending',
         envoyee_le = now(),
         erreur     = format('reservee par %s', p_agent)

   -- Le coeur de la garantie. Une piece deja partie ne se reserve pas :
   --   certified : la DGI l'a enregistree, un renvoi serait un doublon ;
   --   sending   : un envoi est en vol, son issue est inconnue ;
   --   transmise : elle est deja au portail.
   -- Seule une piece qui n'a rien donne — ou dont l'envoi a ete franchement
   -- refuse — peut repartir.
   where certifications.etat in ('pending', 'validating', 'ready', 'error');

  get diagnostics v_reserve = row_count;
  return v_reserve;
end;
$$;

comment on function reserver_piece is
  'Reserve une piece avant de l''envoyer a la DGI. Faux quand elle est deja partie : l''appelant ne doit alors rien envoyer. Deux agents peuvent reserver deux pieces differentes en meme temps.';

create or replace function liberer_piece(
  p_dossier       uuid,
  p_environnement fne_environnement,
  p_identite      text,
  p_motif         text
) returns boolean
language plpgsql security definer set search_path = public as $$
declare
  v_libere boolean;
begin
  -- Repasse en « error » une piece reservee dont l'envoi a ete franchement
  -- refuse — un 4xx, ou un refus forme avant tout appel. Une issue INCONNUE,
  -- elle, reste en « sending » : la DGI a pu enregistrer la facture, et la
  -- liberer autoriserait un second envoi.
  update certifications
     set etat = 'error', erreur = p_motif
   where dossier_id = p_dossier
     and environnement = p_environnement
     and identite = p_identite
     and etat = 'sending';

  get diagnostics v_libere = row_count;
  return v_libere;
end;
$$;

comment on function liberer_piece is
  'Rend une piece reservee apres un refus NET. Une issue inconnue reste « sending » : la liberer autoriserait un doublon.';

-- --- La supervision ---------------------------------------------------------
--
-- Avec vingt clients, un agent tombe ne se voit pas : son journal est sur sa
-- machine, et un journal muet ne se distingue pas d'un service arrete.

create table battements (
  dossier_id      uuid not null references dossiers(id) on delete cascade,
  agent_id        text not null check (length(btrim(agent_id)) > 0),

  quand           timestamptz not null default now(),
  version         text not null default '',
  poste           text not null default '',
  environnement   fne_environnement not null,
  mode            text not null default '',

  sage            text not null default '',
  reseau          text not null default '',

  examinees       bigint not null default 0 check (examinees >= 0),
  envoyees        bigint not null default 0 check (envoyees >= 0),
  en_attente      integer not null default 0 check (en_attente >= 0),
  derniere_activite timestamptz,

  -- Un agent, une ligne. L'historique des battements n'apprendrait rien que le
  -- journal du poste ne dise mieux, et remplirait la base a raison d'une ligne
  -- par minute et par client.
  primary key (dossier_id, agent_id)
);

comment on table battements is
  'Le dernier signe de vie de chaque agent. Plusieurs agents par dossier sont normaux : chacun a sa ligne.';

create or replace view agents_muets as
  select b.dossier_id, d.code as dossier, b.agent_id, b.poste, b.quand,
         now() - b.quand as depuis,
         b.mode, b.environnement
    from battements b
    join dossiers d on d.id = b.dossier_id
   where b.quand < now() - interval '15 minutes'
   order by b.quand;

comment on view agents_muets is
  'Les agents sans signe de vie depuis un quart d''heure. Avec vingt clients, c''est la seule facon de voir lequel est tombe.';

-- --- Qui voit quoi ----------------------------------------------------------

alter table battements enable row level security;

-- Lecture seule depuis le navigateur : l'ecriture appartient a l'agent, qui
-- passe par la cle de service, hors RLS.
create policy battements_lecture on battements
  for select using (est_membre(tenant_du_dossier(dossier_id)));
