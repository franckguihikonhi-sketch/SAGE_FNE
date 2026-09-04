-- ---------------------------------------------------------------------------
-- Ce que l'écran distant peut demander, et ce qu'il ne peut pas décider.
--
-- Une demande n'est pas un ordre. L'agent, seul, lit Sage, tient le registre et
-- parle à la DGI ; il refait TOUS ses contrôles avant d'envoyer quoi que ce
-- soit. Une demande dit « quelqu'un a cliqué », jamais « certifie ».
--
-- La distinction n'est pas théorique. Le registre est la seule mémoire
-- anti-doublon, et une pièce a déjà été certifiée deux fois sur ce dossier
-- parce que deux mémoires se croyaient toutes deux vraies. Le cloud n'en sera
-- pas une troisième.
-- ---------------------------------------------------------------------------

create type demande_etat as enum ('en_attente', 'prise', 'traitee', 'refusee');

create table demandes_certification (
  id             uuid primary key default gen_random_uuid(),
  dossier_id     uuid not null references dossiers(id) on delete cascade,

  -- La même identité que le registre : domaine/DO_DocType/DO_Piece.
  identite       text not null check (length(btrim(identite)) > 0),
  piece          text not null check (length(btrim(piece)) > 0),

  -- Obligatoire, et c'est tout l'objet : la DGI marque paymentMethod
  -- obligatoire, Sage ne le porte pas, et toutes les factures sont longtemps
  -- parties en « deferred » sans que personne l'ait choisi.
  mode_paiement  text not null check (
    mode_paiement in ('cash', 'card', 'check', 'mobile-money', 'transfer', 'deferred')),

  etat           demande_etat not null default 'en_attente',

  -- Qui a cliqué. auth.uid() par défaut : une demande anonyme n'existe pas.
  demande_par    uuid not null default auth.uid(),
  demande_le     timestamptz not null default now(),

  prise_le       timestamptz,
  traitee_le     timestamptz,

  -- Ce que l'agent a répondu, en clair. Un refus doit se lire sans consulter
  -- le journal du poste.
  resultat       text not null default ''
);

comment on table demandes_certification is
  'Une intention exprimée depuis l''écran distant. L''agent la relit, refait ses contrôles, et décide.';

comment on column demandes_certification.etat is
  'en_attente : personne ne l''a encore prise. prise : l''agent l''a réservée et va agir. traitee/refusee : il a répondu.';

-- Une pièce ne peut pas être demandée deux fois tant que la première demande
-- n'est pas retombée. Sans cela, deux clics à dix secondes d'intervalle
-- feraient deux demandes, que l'agent traiterait l'une après l'autre : la
-- seconde serait refusée par le registre, mais autant ne pas la créer.
create unique index demande_unique_en_cours
  on demandes_certification (dossier_id, identite)
  where etat in ('en_attente', 'prise');

create index demandes_a_traiter
  on demandes_certification (dossier_id, demande_le)
  where etat = 'en_attente';

-- --- Ce que la base refuse, quoi qu'en dise l'applicatif --------------------

create or replace function demande_transition_autorisee(avant demande_etat, apres demande_etat)
returns boolean language sql immutable as $$
  select case
    -- Une demande tranchée l'est pour de bon. La rouvrir reviendrait à
    -- rejouer un envoi sans repasser par le registre.
    when avant in ('traitee', 'refusee') then false
    when avant = 'prise' then apres in ('traitee', 'refusee')
    when avant = 'en_attente' then apres in ('prise', 'traitee', 'refusee')
    else false
  end;
$$;

create or replace function garder_demande() returns trigger
language plpgsql as $$
begin
  if tg_op = 'UPDATE' and new.etat is distinct from old.etat
     and not demande_transition_autorisee(old.etat, new.etat) then
    raise exception
      'transition de demande interdite : % -> % (pièce %)', old.etat, new.etat, old.piece;
  end if;

  -- Ni la pièce ni le mode de règlement ne se réécrivent : ce serait changer
  -- ce qui a été demandé après coup, et l'agent agit sur ces deux valeurs.
  if tg_op = 'UPDATE' and (new.identite is distinct from old.identite
                           or new.piece is distinct from old.piece
                           or new.mode_paiement is distinct from old.mode_paiement
                           or new.dossier_id is distinct from old.dossier_id
                           or new.demande_par is distinct from old.demande_par) then
    raise exception 'une demande ne se réécrit pas : créez-en une autre.';
  end if;

  if new.etat = 'prise' and new.prise_le is null then
    new.prise_le := now();
  end if;

  if new.etat in ('traitee', 'refusee') and new.traitee_le is null then
    new.traitee_le := now();
  end if;

  return new;
end;
$$;

create trigger demande_garde
  before insert or update on demandes_certification
  for each row execute function garder_demande();

-- --- Qui peut demander quoi -------------------------------------------------

alter table demandes_certification enable row level security;

create policy demandes_lecture on demandes_certification
  for select using (est_membre(tenant_du_dossier(dossier_id)));

-- Créer une demande est un acte d'exploitation, pas de consultation. Un membre
-- en lecture seule voit les factures et ne peut rien envoyer.
create policy demandes_creation on demandes_certification
  for insert with check (
    peut_ecrire(tenant_du_dossier(dossier_id))
    and demande_par = auth.uid());

-- Personne ne modifie une demande depuis le navigateur : seul l'agent le fait,
-- et il passe par la clé de service, hors RLS. Sans cela, l'écran pourrait
-- marquer « traitee » une demande que rien n'a traitée.
comment on policy demandes_creation on demandes_certification is
  'Insertion seule. La suite appartient à l''agent, qui écrit avec la clé de service.';

-- --- Ce qui attend l'agent --------------------------------------------------

create or replace view demandes_en_attente as
  select d.id, d.dossier_id, d.identite, d.piece, d.mode_paiement,
         d.demande_par, d.demande_le,
         now() - d.demande_le as depuis
    from demandes_certification d
   where d.etat = 'en_attente'
   order by d.demande_le;

comment on view demandes_en_attente is
  'Les clics que l''agent n''a pas encore relus. Il refait ses contrôles avant d''agir : une demande n''est pas un ordre.';
