-- ---------------------------------------------------------------------------
-- Middleware FNE — base d'audit du SaaS
--
-- Ce schéma ne contient RIEN de la base Sage : celle-ci reste en lecture seule
-- et n'est jamais répliquée ici. On n'y garde que ce que Sage ne peut pas
-- porter — l'état de certification d'une pièce, sa référence DGI, et le
-- paramétrage fiscal que le dossier ne permet pas de déduire.
--
-- La clé d'API FNE n'a volontairement aucune colonne. Une clé en base est une
-- clé qui fuite : elle vit dans un gestionnaire de secrets, hors de Postgres.
-- ---------------------------------------------------------------------------

create extension if not exists "pgcrypto";

-- --- Vocabulaire ------------------------------------------------------------

-- Les six états de la chaîne. « sending » est le plus important : il signale un
-- envoi dont l'issue est inconnue, et interdit le renvoi automatique.
create type fne_etat as enum (
  'pending', 'validating', 'ready', 'sending', 'certified', 'error'
);

create type fne_environnement as enum ('test', 'production');

-- TVAC et TVAD valent tous deux 0 % : Sage ne les distingue pas, la règle vient
-- d'ici.
create type regime_tva_zero as enum ('conventional_exemption', 'legal_exemption_tee_rme');

-- Du plus précis au plus général. L'ordre des valeurs est l'ordre de priorité.
create type portee_regle as enum ('article', 'famille', 'client', 'dossier');

-- --- Locataires et accès ----------------------------------------------------

create table tenants (
  id          uuid primary key default gen_random_uuid(),
  nom         text not null check (length(btrim(nom)) > 0),
  ncc         text,
  cree_le     timestamptz not null default now()
);

comment on table tenants is
  'Une entreprise cliente du SaaS. Le NCC est le sien, pas celui de ses clients.';

create type role_membre as enum ('proprietaire', 'exploitant', 'lecteur');

create table membres (
  tenant_id   uuid not null references tenants(id) on delete cascade,
  user_id     uuid not null,
  role        role_membre not null default 'lecteur',
  cree_le     timestamptz not null default now(),
  primary key (tenant_id, user_id)
);

comment on table membres is
  'Qui accède à quel locataire. Toutes les politiques RLS passent par cette table.';

-- --- Dossiers Sage ----------------------------------------------------------

create table dossiers (
  id                uuid primary key default gen_random_uuid(),
  tenant_id         uuid not null references tenants(id) on delete cascade,
  code              text not null check (length(btrim(code)) > 0),
  nom               text not null default '',
  base_sage         text not null default '',
  environnement     fne_environnement not null default 'test',
  point_of_sale     text not null default '',
  establishment     text not null default '',
  template          text not null default 'B2B',
  payment_method    text not null default 'deferred',
  cree_le           timestamptz not null default now(),
  maj_le            timestamptz not null default now(),
  unique (tenant_id, code)
);

comment on column dossiers.base_sage is
  'Nom de la base Sage (« HT »), pour mémoire. Aucune connexion n''est stockée ici.';
comment on column dossiers.environnement is
  'test par défaut : un défaut de production ferait certifier pour de vrai.';

-- --- Le registre des certifications ----------------------------------------

create table certifications (
  id              uuid primary key default gen_random_uuid(),
  dossier_id      uuid not null references dossiers(id) on delete cascade,

  -- domaine / DO_DocType / DO_Piece — insensible à la comptabilisation, qui
  -- fait passer DO_Type de 6 à 7 sur la même pièce.
  identite        text not null check (length(btrim(identite)) > 0),
  piece           text not null,
  do_type         smallint,
  do_doc_type     smallint,
  date_piece      date,
  tiers           text not null default '',
  client_ncc      text not null default '',

  total_ht        numeric(18, 4),
  total_ttc       numeric(18, 4),

  -- SHA-256 du corps envoyé : distingue « déjà certifiée » de « modifiée depuis ».
  empreinte       text not null default '',

  etat            fne_etat not null default 'pending',
  reference_fne   text not null default '',
  token           text not null default '',
  reponse         jsonb,
  erreur          text not null default '',

  envoyee_le      timestamptz,
  certifiee_le    timestamptz,
  cree_le         timestamptz not null default now(),
  maj_le          timestamptz not null default now(),

  -- La garantie anti-doublon, tenue par la base et non par le code appelant.
  unique (dossier_id, identite),

  -- Une pièce certifiée porte forcément sa référence.
  constraint certification_certifiee_a_une_reference
    check (etat <> 'certified' or length(btrim(reference_fne)) > 0)
);

comment on table certifications is
  'Une ligne par pièce Sage. Sage ne porte aucune de ces colonnes et reste en lecture seule.';
comment on constraint certification_certifiee_a_une_reference on certifications is
  'Certifiée sans référence signifierait que la DGI a enregistré une facture dont nous ignorons le numéro.';

create index certifications_par_etat on certifications (dossier_id, etat);
create index certifications_par_piece on certifications (dossier_id, piece);

-- Les envois dont l'issue est inconnue : la première chose à regarder chaque
-- matin. Un index partiel plutôt qu'un balayage.
--
-- Le nom diffère de la vue qui s'appuie dessus : Postgres loge index, tables et
-- vues dans le même espace de noms, et « certifications_en_suspens » ne peut
-- désigner qu'un seul objet.
create index certifications_envois_en_cours on certifications (dossier_id, envoyee_le)
  where etat = 'sending';

-- --- La trace, en ajout seul ------------------------------------------------

create table certification_evenements (
  id                bigint generated always as identity primary key,
  certification_id  uuid not null references certifications(id) on delete cascade,
  etat_avant        fne_etat,
  etat_apres        fne_etat not null,
  message           text not null default '',
  corps             jsonb,
  cree_le           timestamptz not null default now()
);

comment on table certification_evenements is
  'Historique des changements d''état. Ajout seul : ni modification ni suppression.';

create index evenements_par_certification
  on certification_evenements (certification_id, cree_le desc);

-- --- Le paramétrage que Sage ne peut pas porter -----------------------------

create table regles_tva_zero (
  id          uuid primary key default gen_random_uuid(),
  dossier_id  uuid not null references dossiers(id) on delete cascade,
  portee      portee_regle not null,
  cle         text not null default '',
  regime      regime_tva_zero not null,
  note        text not null default '',
  cree_le     timestamptz not null default now(),
  unique (dossier_id, portee, cle),

  -- La règle du dossier vaut pour tout : elle n'a pas de clé. Les autres en ont
  -- forcément une.
  constraint regle_cle_coherente check (
    (portee = 'dossier' and cle = '') or (portee <> 'dossier' and length(btrim(cle)) > 0)
  )
);

comment on table regles_tva_zero is
  'TVAC ou TVAD pour les lignes à 0 %. Sage ne distingue pas les deux : le taux vaut 0 dans les deux cas.';

create table mappings_prelevements (
  id          uuid primary key default gen_random_uuid(),
  dossier_id  uuid not null references dossiers(id) on delete cascade,
  code_sage   text not null check (length(btrim(code_sage)) > 0),
  nom_fne     text not null check (length(btrim(nom_fne)) > 0),
  cree_le     timestamptz not null default now(),
  unique (dossier_id, code_sage)
);

comment on table mappings_prelevements is
  'Un prélèvement ne part en customTaxes que s''il est nommé ici. TA_EdiCode vaut « VAT » même pour l''AIRSI.';
