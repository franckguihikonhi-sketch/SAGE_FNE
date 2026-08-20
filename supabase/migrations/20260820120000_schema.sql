-- Passerelle FNE Sage : schema applicatif.
--
-- Ce que la base conserve, et ce qu'elle ne conserve pas.
--
-- Elle conserve le parametrage d'un dossier (correspondance clients, modes de
-- reglement, format d'import) et l'ENTETE des factures deja converties, afin
-- de detecter les doublons : rien n'empeche aujourd'hui de reimporter deux
-- fois la meme facture dans Sage.
--
-- Elle ne conserve PAS le detail des lignes d'articles. La conversion reste
-- integralement locale au poste ; seul le strict necessaire au parametrage et
-- au controle des doublons remonte.

create extension if not exists "pgcrypto";

-- Horodatage de derniere modification, pose sur chaque table.
create or replace function public.touch_updated_at()
returns trigger
language plpgsql
as $$
begin
  new.updated_at := now();
  return new;
end;
$$;

-- --------------------------------------------------------------------------
-- Dossiers et acces
-- --------------------------------------------------------------------------

-- Un dossier = une entreprise emettrice de factures FNE, identifiee par son NCC.
create table public.dossiers (
  id uuid primary key default gen_random_uuid(),
  nom text not null check (length(btrim(nom)) > 0),
  -- NCC de l'entreprise sur la plateforme FNE (ex. 2304903U).
  ncc text not null,
  cree_par uuid not null references auth.users (id) on delete restrict,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint dossiers_ncc_unique unique (ncc)
);

create trigger dossiers_touch before update on public.dossiers
  for each row execute function public.touch_updated_at();

-- Qui a acces a quel dossier. Un membre "gestionnaire" administre les acces ;
-- un "operateur" convertit et complete les correspondances.
create table public.membres (
  dossier_id uuid not null references public.dossiers (id) on delete cascade,
  utilisateur_id uuid not null references auth.users (id) on delete cascade,
  role text not null default 'operateur' check (role in ('gestionnaire', 'operateur')),
  created_at timestamptz not null default now(),
  primary key (dossier_id, utilisateur_id)
);

create index membres_utilisateur_idx on public.membres (utilisateur_id);

-- Vrai si l'utilisateur courant est membre du dossier. Utilisee par toutes les
-- politiques RLS : `security definer` evite la recursion sur `membres`.
create or replace function public.est_membre(dossier uuid)
returns boolean
language sql
stable
security definer
set search_path = public
as $$
  select exists (
    select 1 from public.membres
    where membres.dossier_id = dossier
      and membres.utilisateur_id = auth.uid()
  );
$$;

create or replace function public.est_gestionnaire(dossier uuid)
returns boolean
language sql
stable
security definer
set search_path = public
as $$
  select exists (
    select 1 from public.membres
    where membres.dossier_id = dossier
      and membres.utilisateur_id = auth.uid()
      and membres.role = 'gestionnaire'
  );
$$;

-- --------------------------------------------------------------------------
-- Parametrage
-- --------------------------------------------------------------------------

-- Parametres Sage du dossier : ce que l'ecran de gauche de la passerelle porte
-- aujourd'hui dans le stockage local du navigateur.
create table public.parametres (
  dossier_id uuid primary key references public.dossiers (id) on delete cascade,
  profil_id text not null default 'sage100-import-export',
  depot text not null default '',
  souche text not null default '1',
  -- sequence : annee + numero FNE ; reference : reference complete ; vide : Sage numerote.
  numero_piece text not null default 'sequence'
    check (numero_piece in ('sequence', 'reference', 'vide')),
  compte_par_defaut text not null default '',
  updated_at timestamptz not null default now()
);

create trigger parametres_touch before update on public.parametres
  for each row execute function public.touch_updated_at();

-- Correspondance client FNE -> compte tiers Sage. Le NCC prime sur le nom,
-- mais un client particulier (B2C) n'a pas de NCC : le nom sert alors de cle.
create table public.comptes_tiers (
  id uuid primary key default gen_random_uuid(),
  dossier_id uuid not null references public.dossiers (id) on delete cascade,
  ncc text not null default '',
  nom text not null default '',
  compte_sage text not null check (length(btrim(compte_sage)) > 0),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint comptes_tiers_identifiant check (
    length(btrim(ncc)) > 0 or length(btrim(nom)) > 0
  )
);

create trigger comptes_tiers_touch before update on public.comptes_tiers
  for each row execute function public.touch_updated_at();

-- Un meme NCC ne peut pointer que sur un compte par dossier. Les index partiels
-- laissent cohabiter les clients sans NCC, identifies par leur nom.
create unique index comptes_tiers_ncc_unique
  on public.comptes_tiers (dossier_id, lower(btrim(ncc)))
  where length(btrim(ncc)) > 0;

create unique index comptes_tiers_nom_unique
  on public.comptes_tiers (dossier_id, lower(btrim(nom)))
  where length(btrim(ncc)) = 0;

-- Mode de paiement FNE (cash, card, check, mobile-money, transfer, deferred)
-- vers le code reglement du dossier Sage.
create table public.modes_reglement (
  dossier_id uuid not null references public.dossiers (id) on delete cascade,
  code_fne text not null check (
    code_fne in ('cash', 'card', 'check', 'mobile-money', 'transfer', 'deferred')
  ),
  code_sage text not null check (length(btrim(code_sage)) > 0),
  updated_at timestamptz not null default now(),
  primary key (dossier_id, code_fne)
);

create trigger modes_reglement_touch before update on public.modes_reglement
  for each row execute function public.touch_updated_at();

-- Format d'import propre au dossier. Le profil est entierement declaratif du
-- cote applicatif : le stocker en JSON permet d'adapter un dossier Sage sans
-- toucher au code ni redeployer.
create table public.profils_import (
  id uuid primary key default gen_random_uuid(),
  dossier_id uuid not null references public.dossiers (id) on delete cascade,
  profil_id text not null,
  libelle text not null,
  definition jsonb not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  constraint profils_import_unique unique (dossier_id, profil_id)
);

create trigger profils_import_touch before update on public.profils_import
  for each row execute function public.touch_updated_at();

-- --------------------------------------------------------------------------
-- Historique des conversions
-- --------------------------------------------------------------------------

create table public.conversions (
  id uuid primary key default gen_random_uuid(),
  dossier_id uuid not null references public.dossiers (id) on delete cascade,
  utilisateur_id uuid references auth.users (id) on delete set null,
  fichier text not null,
  -- fne-json ou tableau : le second ne porte pas le detail des articles.
  source text not null check (source in ('fne-json', 'tableau')),
  profil_id text not null,
  nb_factures integer not null default 0 check (nb_factures >= 0),
  nb_avoirs integer not null default 0 check (nb_avoirs >= 0),
  nb_lignes integer not null default 0 check (nb_lignes >= 0),
  total_ht numeric(18, 4) not null default 0,
  total_tva numeric(18, 4) not null default 0,
  total_ttc numeric(18, 4) not null default 0,
  nb_erreurs integer not null default 0 check (nb_erreurs >= 0),
  nb_avertissements integer not null default 0 check (nb_avertissements >= 0),
  -- Renseigne quand l'utilisateur confirme avoir importe le fichier dans Sage.
  importe_le timestamptz,
  created_at timestamptz not null default now()
);

create index conversions_dossier_idx on public.conversions (dossier_id, created_at desc);

-- Entete des factures converties : de quoi detecter un doublon et retrouver
-- une piece, sans conserver le detail commercial des lignes.
create table public.factures (
  id uuid primary key default gen_random_uuid(),
  dossier_id uuid not null references public.dossiers (id) on delete cascade,
  conversion_id uuid not null references public.conversions (id) on delete cascade,
  -- Reference certifiee par la DGI (ex. 2304903U26000000889), unique par dossier.
  reference_fne text not null check (length(btrim(reference_fne)) > 0),
  numero_piece text not null default '',
  nature text not null check (nature in ('FACTURE', 'AVOIR')),
  date_facture date,
  client_ncc text not null default '',
  client_nom text not null default '',
  compte_sage text not null default '',
  total_ht numeric(18, 4) not null default 0,
  total_tva numeric(18, 4) not null default 0,
  total_ttc numeric(18, 4) not null default 0,
  created_at timestamptz not null default now()
);

-- La garantie anti-doublon : une reference FNE ne peut entrer qu'une fois par dossier.
create unique index factures_reference_unique
  on public.factures (dossier_id, reference_fne);

create index factures_conversion_idx on public.factures (conversion_id);
create index factures_client_idx on public.factures (dossier_id, client_ncc);
