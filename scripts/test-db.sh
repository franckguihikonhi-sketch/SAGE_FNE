#!/usr/bin/env bash
# Applique les migrations Supabase sur un PostgreSQL local et joue les
# controles d'isolation. Le schema `auth` de Supabase est reproduit a minima
# par supabase/tests/stub-auth.sql.
set -euo pipefail

BASE="${1:-passerelle_test}"
PSQL=(su postgres -c)

echo "Base de test : $BASE"
${PSQL[@]} "psql -q -c 'drop database if exists $BASE'"
${PSQL[@]} "psql -q -c 'create database $BASE'"

run() {
  ${PSQL[@]} "psql -q -v ON_ERROR_STOP=1 -d $BASE -f $(pwd)/$1"
}

run supabase/tests/stub-auth.sql
for migration in supabase/migrations/*.sql; do
  echo "  migration $(basename "$migration")"
  run "$migration"
done

# Les assertions vivent dans leur propre schema, accessible au role de test.
${PSQL[@]} "psql -q -v ON_ERROR_STOP=1 -d $BASE -c 'create schema tests; grant usage on schema tests to authenticated'"
echo "Controles :"
${PSQL[@]} "psql -q -v ON_ERROR_STOP=1 -d $BASE -f $(pwd)/supabase/tests/rls.sql" 2>&1 |
  sed -E 's#^psql:[^ ]+ ##; s/^NOTICE:  //'
