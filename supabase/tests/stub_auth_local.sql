create schema if not exists auth;
create table if not exists auth.users (id uuid primary key);
create or replace function auth.uid() returns uuid
language sql stable as $$ select nullif(current_setting('request.jwt.claim.sub', true), '')::uuid $$;
-- Les rôles sont partagés par toute l'instance PostgreSQL, pas par la base :
-- une seconde exécution sur le même serveur les retrouve. Idempotent, sans
-- quoi rejouer les migrations en local échoue là où la CI, qui part d'un
-- conteneur neuf, passait.
do $$
begin
  if not exists (select 1 from pg_roles where rolname = 'anon') then
    create role anon nologin;
  end if;
  if not exists (select 1 from pg_roles where rolname = 'authenticated') then
    create role authenticated nologin;
  end if;
end
$$;
