-- ---------------------------------------------------------------------------
-- Les règles de TVA 0 %, versionnées et en ajout seul
--
-- La table d'origine portait une colonne « regime » qui disait à la fois quel
-- code envoyer et pourquoi. Les deux ne sont pas la même chose : TVAD est un
-- code FNE, l'exonération d'un acheteur au régime TEE ou RME en est le
-- fondement. Un jour la DGI précisera sa nomenclature, ou un acheteur perdra
-- son agrément : il faudra pouvoir changer l'un sans réécrire l'autre.
--
-- Elle était aussi modifiable en place. Or une facture certifiée l'est pour
-- toujours. Corriger une règle en 2027 aurait effacé celle sous laquelle des
-- factures de 2026 sont parties, et la question « sur quel fondement cette
-- facture-là a-t-elle été exonérée » serait restée sans réponse.
--
-- D'où cette table : une règle y est une suite de versions, chacune écrite une
-- fois pour toutes. Modifier, c'est ajouter une version. Révoquer aussi.
-- ---------------------------------------------------------------------------

-- --- Vocabulaire ------------------------------------------------------------

-- Ce qui part chez la DGI, et rien d'autre.
create type code_tva_zero as enum ('tvac', 'tvad');

comment on type code_tva_zero is
  'Le code FNE d''une ligne à 0 %. Il ne dit pas pourquoi : voir fondement_exoneration.';

-- Ce qui autorise ce code. Un même code peut avoir plusieurs fondements, et le
-- fondement est ce qu''un contrôle demandera.
create type fondement_exoneration as enum (
  -- L'acheteur est déclaré TEE ou RME. Déclaré : jamais déduit d'un historique
  -- de factures — une facture à 0 % ne prouve pas un régime, elle peut être une
  -- erreur de saisie répétée.
  'regime_acheteur',
  -- Le produit lui-même est exonéré par la loi.
  'exoneration_legale_produit',
  -- Une convention ou un agrément nommé.
  'convention',
  -- Autre fondement établi, décrit dans le motif.
  'autre_valide'
);

create type etat_regle as enum (
  -- Écrite, pas validée. Elle ne produit aucun code : la pièce reste bloquée.
  -- C'est l'état par défaut, et le seul sûr.
  'brouillon',
  'validee',
  -- Révoquée. Elle ne produit plus rien, et reste au registre : des factures
  -- sont parties sous elle.
  'revoquee'
);

-- Le régime de l'acheteur passe devant tout le reste : c'est une qualité de la
-- personne, pas du produit qu'on lui vend. L'ordre des valeurs est l'ordre de
-- priorité.
alter type portee_regle rename to portee_regle_ancienne;

create type portee_regle as enum (
  'regime_acheteur', 'article', 'famille', 'client', 'dossier'
);

-- --- La table ---------------------------------------------------------------

alter table regles_tva_zero rename to regles_tva_zero_ancienne;
alter table regles_tva_zero_ancienne rename constraint regle_cle_coherente
  to regle_cle_coherente_ancienne;

create table regles_tva_zero (
  id            uuid primary key default gen_random_uuid(),
  dossier_id    uuid not null references dossiers(id) on delete cascade,

  -- Identifiant stable d'une version à l'autre. Le même que celui du registre
  -- fichier du middleware, pour que les deux se recoupent.
  regle_id      text not null check (length(btrim(regle_id)) > 0),
  version       integer not null check (version >= 1),

  portee        portee_regle not null,
  cle           text not null default '',

  -- Nuls tant que rien n'est arrêté. Une règle sans code ne produit rien, et
  -- c'est voulu : le code d'une exonération ne se devine pas.
  code          code_tva_zero,
  fondement     fondement_exoneration,

  -- Le régime déclaré de l'acheteur, pour la portée qui va avec.
  regime        text not null default '',

  etat          etat_regle not null default 'brouillon',

  -- La preuve et sa provenance.
  validee_par   text not null default '',
  validee_le    timestamptz,
  reference     text not null default '',
  empreinte_justificatif text not null default '',
  motif         text not null default '',

  -- Bornes de validité, quand la preuve en porte : un agrément a une date de
  -- fin, et une facture postérieure ne peut pas s'en réclamer.
  valide_du     timestamptz,
  valide_au     timestamptz,

  note          text not null default '',
  cree_le       timestamptz not null default now(),

  -- Une version s'écrit une fois. Rejouer la même est un doublon, pas une
  -- correction.
  unique (dossier_id, regle_id, version),

  -- La règle du dossier vaut pour tout : elle n'a pas de clé. Celle qui déclare
  -- un régime porte les deux — le compte de l'acheteur en clé, son régime à
  -- côté : c'est une qualité de cette personne-là, et elle se déclare compte par
  -- compte. Les autres portent une clé et pas de régime.
  constraint regle_cle_coherente check (
    case portee
      when 'dossier'         then cle = '' and regime = ''
      when 'regime_acheteur' then length(btrim(cle)) > 0 and length(btrim(regime)) > 0
      else length(btrim(cle)) > 0 and regime = ''
    end
  ),

  -- Une règle validée porte sa preuve. Sans cela « validée » ne veut rien dire
  -- de plus que « écrite », et c'est exactement la confusion qui a fait partir
  -- des lignes à 0 % sous un code que personne n'avait arrêté.
  constraint regle_validee_porte_sa_preuve check (
    etat <> 'validee'
    or (code is not null
        and fondement is not null
        and length(btrim(validee_par)) > 0
        and validee_le is not null
        and (length(btrim(reference)) > 0 or length(btrim(empreinte_justificatif)) > 0))
  ),

  constraint bornes_ordonnees check (
    valide_du is null or valide_au is null or valide_du <= valide_au
  )
);

comment on table regles_tva_zero is
  'Les règles de classification des lignes à 0 %, en versions. Une version qui a servi à certifier ne se réécrit jamais.';
comment on column regles_tva_zero.code is
  'Le code FNE seul. Null tant qu''aucun code n''est arrêté : la ligne reste alors bloquée.';
comment on column regles_tva_zero.fondement is
  'Ce qui autorise le code. Ne se déduit pas du code, et ne se déduit jamais de l''historique des factures.';
comment on column regles_tva_zero.etat is
  'brouillon par défaut. Une règle non validée ne produit aucun code.';

create index regles_par_dossier_et_portee
  on regles_tva_zero (dossier_id, portee, cle);

create index regles_par_identite
  on regles_tva_zero (dossier_id, regle_id, version desc);

-- --- Ce qui a été écrit reste écrit -----------------------------------------

create or replace function regles_en_ajout_seul() returns trigger
language plpgsql as $$
begin
  raise exception
    'regles_tva_zero est en ajout seul : une version ne se modifie ni ne s''efface.'
    using hint = 'Corriger une règle, c''est en ajouter une version. La révoquer aussi. Des factures sont parties sous la version précédente.';
end;
$$;

create trigger regles_immuables
  before update or delete on regles_tva_zero
  for each row execute function regles_en_ajout_seul();

-- Une version s'ajoute après la dernière, jamais avant ni à sa place. Sans
-- cela, on pourrait glisser une version 1 « validée » derrière une version 2
-- révoquée, et faire repartir des lignes que quelqu'un avait arrêtées.
create or replace function regle_version_suit_la_derniere() returns trigger
language plpgsql as $$
declare
  derniere integer;
begin
  select max(version) into derniere
    from regles_tva_zero
   where dossier_id = new.dossier_id and regle_id = new.regle_id;

  if derniere is not null and new.version <> derniere + 1 then
    raise exception
      'La règle % est en version % : la suivante est %, pas %.',
      new.regle_id, derniere, derniere + 1, new.version
      using hint = 'Les versions se suivent. Une version intercalée réécrirait une histoire déjà servie.';
  end if;

  if derniere is null and new.version <> 1 then
    raise exception 'Une règle commence en version 1, pas %.', new.version;
  end if;

  return new;
end;
$$;

create trigger regle_versions_ordonnees
  before insert on regles_tva_zero
  for each row execute function regle_version_suit_la_derniere();

-- --- Lire l'état courant ----------------------------------------------------

create view regles_tva_zero_courantes as
  select distinct on (dossier_id, regle_id) *
    from regles_tva_zero
   order by dossier_id, regle_id, version desc;

comment on view regles_tva_zero_courantes is
  'La dernière version de chaque règle. Les précédentes restent lisibles : elles ont certifié des factures.';

-- Sur les colonnes plutôt que sur la ligne entière : la vue courante et la
-- table n'ont pas le même type composite, et une règle doit pouvoir se juger
-- des deux côtés avec la même fonction.
create or replace function regle_applicable(
  l_etat etat_regle,
  le_code code_tva_zero,
  du timestamptz,
  au timestamptz,
  quand timestamptz
) returns boolean language sql immutable as $$
  select l_etat = 'validee'
     and le_code is not null
     and (du is null or quand >= du)
     and (au is null or quand <= au);
$$;

comment on function regle_applicable is
  'Vrai quand la règle produit son code à cette date. Quatre conditions, l''absence d''une seule bloque.';

-- --- Ce qui mérite une relecture --------------------------------------------

-- Pas un refus : un signalement. Une règle fondée sur le régime de l'acheteur
-- qui ne porte pas TVAD est probablement une erreur de saisie — mais c'est à un
-- humain de le dire, pas à une contrainte de le décréter.
create view regles_tva_zero_a_relire as
  select r.*,
         case
           when r.fondement = 'regime_acheteur' and r.code <> 'tvad'
             then 'fondée sur le régime de l''acheteur mais ne portant pas TVAD'
           when r.etat = 'validee' and r.valide_au is not null and r.valide_au < now()
             then 'validée mais expirée : les lignes qu''elle couvrait sont de nouveau bloquées'
           when r.etat = 'brouillon'
             then 'en brouillon : elle ne produit aucun code'
         end as observation
    from regles_tva_zero_courantes r
   where (r.fondement = 'regime_acheteur' and r.code <> 'tvad')
      or (r.etat = 'validee' and r.valide_au is not null and r.valide_au < now())
      or r.etat = 'brouillon';

comment on view regles_tva_zero_a_relire is
  'Les règles douteuses ou dormantes. La vue expose les faits, elle ne conclut pas à leur place.';

-- --- Sous quelle version une facture est-elle partie -------------------------

-- Sans cela, la question « pourquoi cette ligne-là est-elle exonérée » n'a de
-- réponse que tant que la règle n'a pas bougé. C'est-à-dire pas assez longtemps.
create table certification_regles_appliquees (
  id                bigint generated always as identity primary key,
  certification_id  uuid not null references certifications(id) on delete cascade,

  -- La version exacte, pas la règle : c'est tout l'objet de la table.
  regle             uuid not null references regles_tva_zero(id),

  -- De quelle ligne de la pièce il s'agit.
  ligne             integer not null check (ligne >= 0),
  article           text not null default '',

  cree_le           timestamptz not null default now(),

  unique (certification_id, ligne, regle)
);

comment on table certification_regles_appliquees is
  'Quelle version de quelle règle a classé quelle ligne. Une facture certifiée garde sa justification même quand la règle change ensuite.';

create index regles_appliquees_par_certification
  on certification_regles_appliquees (certification_id);

create index regles_appliquees_par_regle
  on certification_regles_appliquees (regle);

create trigger regles_appliquees_immuables
  before update or delete on certification_regles_appliquees
  for each row execute function regles_en_ajout_seul();

-- --- Accès ------------------------------------------------------------------

alter table regles_tva_zero enable row level security;
alter table certification_regles_appliquees enable row level security;

create policy regles_lecture on regles_tva_zero
  for select using (est_membre(tenant_du_dossier(dossier_id)));

-- Pas de « for all » : l'écriture est un ajout, et rien d'autre. Le déclencheur
-- refuse déjà update et delete ; l'absence de politique le dit une seconde fois.
create policy regles_ajout on regles_tva_zero
  for insert with check (peut_ecrire(tenant_du_dossier(dossier_id)));

create policy regles_appliquees_lecture on certification_regles_appliquees
  for select using (
    exists (
      select 1 from certifications c
       where c.id = certification_regles_appliquees.certification_id
         and est_membre(tenant_du_dossier(c.dossier_id))
    )
  );

create policy regles_appliquees_ajout on certification_regles_appliquees
  for insert with check (
    exists (
      select 1 from certifications c
       where c.id = certification_regles_appliquees.certification_id
         and peut_ecrire(tenant_du_dossier(c.dossier_id))
    )
  );

-- --- L'ancienne table -------------------------------------------------------

-- Ses lignes sont reprises en brouillon, jamais en validée : leur colonne
-- « regime » mêlait le code et le fondement, et personne n'a validé la
-- traduction de l'un vers l'autre. Les promouvoir automatiquement, ce serait
-- inventer la validation que cette migration existe précisément pour exiger.
insert into regles_tva_zero (
  dossier_id, regle_id, version, portee, cle, code, fondement, etat, note, cree_le
)
select a.dossier_id,
       lower(a.portee::text || case when a.cle = '' then '' else '-' || a.cle end),
       1,
       a.portee::text::portee_regle,
       a.cle,
       case a.regime
         when 'conventional_exemption'   then 'tvac'::code_tva_zero
         when 'legal_exemption_tee_rme'  then 'tvad'::code_tva_zero
       end,
       null,
       'brouillon',
       format('Reprise du paramétrage antérieur (regime = %s). Le code proposé n''est pas validé : '
              || 'l''ancienne colonne ne distinguait pas le code FNE de son fondement juridique.',
              a.regime),
       a.cree_le
  from regles_tva_zero_ancienne a;

drop table regles_tva_zero_ancienne;
drop type portee_regle_ancienne;
drop type regime_tva_zero;
