-- Isolation des dossiers.
--
-- La cle anonyme de Supabase est publique : elle est embarquee dans la page.
-- C'est donc la RLS, et elle seule, qui empeche un utilisateur de lire le
-- dossier d'une autre entreprise. Chaque table est verrouillee par defaut et
-- n'ouvre que ce qui passe par `est_membre`.

alter table public.dossiers enable row level security;
alter table public.membres enable row level security;
alter table public.parametres enable row level security;
alter table public.comptes_tiers enable row level security;
alter table public.modes_reglement enable row level security;
alter table public.profils_import enable row level security;
alter table public.conversions enable row level security;
alter table public.factures enable row level security;

-- --------------------------------------------------------------------------
-- Dossiers
-- --------------------------------------------------------------------------

-- Le createur est ajoute comme membre juste apres l'insertion : sans le second
-- terme, `insert ... returning` echouerait, la lecture etant refusee a l'instant
-- ou l'adhesion n'existe pas encore.
create policy dossiers_lecture on public.dossiers
  for select to authenticated
  using (public.est_membre(id) or cree_par = auth.uid());

-- Tout utilisateur connecte peut creer son dossier ; il s'y ajoute ensuite
-- comme gestionnaire (voir membres_creation).
create policy dossiers_creation on public.dossiers
  for insert to authenticated
  with check (cree_par = auth.uid());

create policy dossiers_modification on public.dossiers
  for update to authenticated
  using (public.est_gestionnaire(id))
  with check (public.est_gestionnaire(id));

create policy dossiers_suppression on public.dossiers
  for delete to authenticated
  using (public.est_gestionnaire(id));

-- --------------------------------------------------------------------------
-- Membres
-- --------------------------------------------------------------------------

create policy membres_lecture on public.membres
  for select to authenticated
  using (public.est_membre(dossier_id));

-- Deux cas legitimes : le createur du dossier s'y inscrit, ou un gestionnaire
-- ajoute un collegue.
create policy membres_creation on public.membres
  for insert to authenticated
  with check (
    public.est_gestionnaire(dossier_id)
    or exists (
      select 1 from public.dossiers
      where dossiers.id = membres.dossier_id
        and dossiers.cree_par = auth.uid()
    )
  );

create policy membres_modification on public.membres
  for update to authenticated
  using (public.est_gestionnaire(dossier_id))
  with check (public.est_gestionnaire(dossier_id));

create policy membres_suppression on public.membres
  for delete to authenticated
  using (public.est_gestionnaire(dossier_id));

-- --------------------------------------------------------------------------
-- Parametrage : lisible et modifiable par tout membre du dossier
-- --------------------------------------------------------------------------

create policy parametres_acces on public.parametres
  for all to authenticated
  using (public.est_membre(dossier_id))
  with check (public.est_membre(dossier_id));

create policy comptes_tiers_acces on public.comptes_tiers
  for all to authenticated
  using (public.est_membre(dossier_id))
  with check (public.est_membre(dossier_id));

create policy modes_reglement_acces on public.modes_reglement
  for all to authenticated
  using (public.est_membre(dossier_id))
  with check (public.est_membre(dossier_id));

create policy profils_import_acces on public.profils_import
  for all to authenticated
  using (public.est_membre(dossier_id))
  with check (public.est_membre(dossier_id));

-- --------------------------------------------------------------------------
-- Historique : ajout et lecture, sans reecriture
-- --------------------------------------------------------------------------

create policy conversions_lecture on public.conversions
  for select to authenticated
  using (public.est_membre(dossier_id));

create policy conversions_creation on public.conversions
  for insert to authenticated
  with check (public.est_membre(dossier_id) and utilisateur_id = auth.uid());

-- Seule la confirmation d'import se met a jour apres coup.
create policy conversions_modification on public.conversions
  for update to authenticated
  using (public.est_membre(dossier_id))
  with check (public.est_membre(dossier_id));

create policy conversions_suppression on public.conversions
  for delete to authenticated
  using (public.est_gestionnaire(dossier_id));

create policy factures_lecture on public.factures
  for select to authenticated
  using (public.est_membre(dossier_id));

create policy factures_creation on public.factures
  for insert to authenticated
  with check (public.est_membre(dossier_id));

-- Une facture certifiee ne se modifie pas : elle se supprime avec sa
-- conversion, si le gestionnaire annule un import.
create policy factures_suppression on public.factures
  for delete to authenticated
  using (public.est_gestionnaire(dossier_id));

-- --------------------------------------------------------------------------
-- Droits
-- --------------------------------------------------------------------------
-- Supabase accorde ces droits par defaut sur le schema public, mais un projet
-- durci peut les avoir revoques : les poser explicitement rend la migration
-- autonome. La RLS ci-dessus reste seule juge de ce que chaque ligne autorise.

grant usage on schema public to anon, authenticated;

grant select, insert, update, delete on
  public.dossiers, public.membres, public.parametres, public.comptes_tiers,
  public.modes_reglement, public.profils_import, public.conversions, public.factures
  to authenticated;

grant execute on function public.est_membre(uuid) to authenticated;
grant execute on function public.est_gestionnaire(uuid) to authenticated;
