-- Reproduction locale du strict minimum fourni par Supabase : le schema auth,
-- la table des utilisateurs, auth.uid() et les roles anon / authenticated.
-- Ce fichier ne fait PAS partie des migrations : Supabase fournit deja tout cela.

create schema if not exists auth;

create table if not exists auth.users (
  id uuid primary key default gen_random_uuid(),
  email text unique
);

-- En production, auth.uid() lit l'identifiant porte par le jeton JWT.
-- Ici il est pose par le test via `set local test.user_id`.
create or replace function auth.uid()
returns uuid
language sql
stable
as $$
  select nullif(current_setting('test.user_id', true), '')::uuid;
$$;

do $$
begin
  if not exists (select 1 from pg_roles where rolname = 'anon') then
    create role anon nologin;
  end if;
  if not exists (select 1 from pg_roles where rolname = 'authenticated') then
    create role authenticated nologin;
  end if;
end;
$$;

grant usage on schema auth to anon, authenticated;
grant select on auth.users to anon, authenticated;
grant execute on function auth.uid() to anon, authenticated;
