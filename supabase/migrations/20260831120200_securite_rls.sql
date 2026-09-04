-- ---------------------------------------------------------------------------
-- Cloisonnement des locataires.
--
-- Un SaaS de facturation où un client verrait les factures d'un autre n'est pas
-- un incident, c'est la fin du produit. Toutes les tables sont donc fermées par
-- défaut, et l'accès passe par l'appartenance déclarée dans « membres ».
-- ---------------------------------------------------------------------------

create or replace function est_membre(p_tenant uuid)
returns boolean language sql stable security definer set search_path = public as $$
  select exists (
    select 1 from membres
     where membres.tenant_id = p_tenant
       and membres.user_id = auth.uid()
  );
$$;

create or replace function peut_ecrire(p_tenant uuid)
returns boolean language sql stable security definer set search_path = public as $$
  select exists (
    select 1 from membres
     where membres.tenant_id = p_tenant
       and membres.user_id = auth.uid()
       and membres.role in ('proprietaire', 'exploitant')
  );
$$;

create or replace function tenant_du_dossier(p_dossier uuid)
returns uuid language sql stable security definer set search_path = public as $$
  select tenant_id from dossiers where id = p_dossier;
$$;

alter table tenants                  enable row level security;
alter table membres                  enable row level security;
alter table dossiers                 enable row level security;
alter table certifications           enable row level security;
alter table certification_evenements enable row level security;
alter table regles_tva_zero          enable row level security;
alter table mappings_prelevements    enable row level security;

-- Aucune politique « for all using (true) » nulle part : chaque accès est motivé.

create policy tenants_lecture on tenants
  for select using (est_membre(id));

create policy tenants_maj on tenants
  for update using (peut_ecrire(id)) with check (peut_ecrire(id));

create policy membres_lecture on membres
  for select using (est_membre(tenant_id));

create policy membres_gestion on membres
  for all
  using (exists (
    select 1 from membres m
     where m.tenant_id = membres.tenant_id
       and m.user_id = auth.uid()
       and m.role = 'proprietaire'))
  with check (exists (
    select 1 from membres m
     where m.tenant_id = membres.tenant_id
       and m.user_id = auth.uid()
       and m.role = 'proprietaire'));

create policy dossiers_lecture on dossiers
  for select using (est_membre(tenant_id));

create policy dossiers_ecriture on dossiers
  for all using (peut_ecrire(tenant_id)) with check (peut_ecrire(tenant_id));

create policy certifications_lecture on certifications
  for select using (est_membre(tenant_du_dossier(dossier_id)));

create policy certifications_ecriture on certifications
  for all
  using (peut_ecrire(tenant_du_dossier(dossier_id)))
  with check (peut_ecrire(tenant_du_dossier(dossier_id)));

-- L'historique se lit, il ne s'écrit pas depuis l'extérieur : seul le
-- déclencheur y ajoute des lignes.
create policy evenements_lecture on certification_evenements
  for select using (exists (
    select 1 from certifications c
     where c.id = certification_evenements.certification_id
       and est_membre(tenant_du_dossier(c.dossier_id))));

create policy regles_lecture on regles_tva_zero
  for select using (est_membre(tenant_du_dossier(dossier_id)));

create policy regles_ecriture on regles_tva_zero
  for all
  using (peut_ecrire(tenant_du_dossier(dossier_id)))
  with check (peut_ecrire(tenant_du_dossier(dossier_id)));

create policy mappings_lecture on mappings_prelevements
  for select using (est_membre(tenant_du_dossier(dossier_id)));

create policy mappings_ecriture on mappings_prelevements
  for all
  using (peut_ecrire(tenant_du_dossier(dossier_id)))
  with check (peut_ecrire(tenant_du_dossier(dossier_id)));

-- Le rôle « anon » n'a rien à faire ici : aucune de ces données n'est publique.
revoke all on all tables in schema public from anon;
revoke all on all functions in schema public from anon;
