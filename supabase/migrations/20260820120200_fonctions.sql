-- Operations metier appelees depuis la passerelle.

-- Cree un dossier, y inscrit son createur comme gestionnaire et pose ses
-- parametres par defaut. En une transaction : sans cela, un dossier cree puis
-- non rattache resterait inaccessible a son propre auteur, la RLS de lecture
-- exigeant deja d'en etre membre.
create or replace function public.creer_dossier(nom text, ncc text)
returns public.dossiers
language plpgsql
security invoker
set search_path = public
as $$
declare
  dossier public.dossiers;
begin
  insert into public.dossiers (nom, ncc, cree_par)
  values (nom, ncc, auth.uid())
  returning * into dossier;

  insert into public.membres (dossier_id, utilisateur_id, role)
  values (dossier.id, auth.uid(), 'gestionnaire');

  insert into public.parametres (dossier_id) values (dossier.id);

  return dossier;
end;
$$;

-- Parmi les references d'un export, celles deja converties pour ce dossier.
-- La passerelle s'en sert pour prevenir un double import avant de generer le
-- fichier, plutot que de laisser Sage refuser les pieces une a une.
create or replace function public.references_deja_importees(dossier uuid, refs text[])
returns table (reference_fne text, importe_le timestamptz, fichier text)
language sql
stable
security invoker
set search_path = public
as $$
  select f.reference_fne, c.created_at, c.fichier
  from public.factures f
  join public.conversions c on c.id = f.conversion_id
  where f.dossier_id = dossier
    and f.reference_fne = any (refs)
  order by c.created_at desc;
$$;

-- Remplace la table de correspondance clients d'un dossier en une fois.
-- `jsonb` plutot qu'un tableau de lignes : c'est la forme que la passerelle
-- manipule deja cote navigateur.
create or replace function public.enregistrer_comptes_tiers(dossier uuid, entrees jsonb)
returns integer
language plpgsql
security invoker
set search_path = public
as $$
declare
  nombre integer;
begin
  insert into public.comptes_tiers (dossier_id, ncc, nom, compte_sage)
  select
    dossier,
    coalesce(entree ->> 'ncc', ''),
    coalesce(entree ->> 'nom', ''),
    entree ->> 'compte_sage'
  from jsonb_array_elements(entrees) as entree
  where length(btrim(coalesce(entree ->> 'compte_sage', ''))) > 0
  on conflict do nothing;

  get diagnostics nombre = row_count;
  return nombre;
end;
$$;

-- Vue de synthese : ou en est chaque dossier.
create or replace view public.tableau_de_bord
with (security_invoker = true)
as
  select
    d.id as dossier_id,
    d.nom,
    d.ncc,
    (select count(*) from public.comptes_tiers ct where ct.dossier_id = d.id) as comptes_tiers,
    (select count(*) from public.conversions c where c.dossier_id = d.id) as conversions,
    (select count(*) from public.factures f where f.dossier_id = d.id) as factures,
    (select max(c.created_at) from public.conversions c where c.dossier_id = d.id) as derniere_conversion
  from public.dossiers d;

-- La vue et les fonctions s'executent avec les droits de l'appelant
-- (`security_invoker`, `security invoker`) : la RLS des tables sous-jacentes
-- continue de s'appliquer.
grant select on public.tableau_de_bord to authenticated;
grant execute on function public.creer_dossier(text, text) to authenticated;
grant execute on function public.references_deja_importees(uuid, text[]) to authenticated;
grant execute on function public.enregistrer_comptes_tiers(uuid, jsonb) to authenticated;
