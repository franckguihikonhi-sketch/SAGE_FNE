#!/usr/bin/env bash
# Applique les migrations sur un PostgreSQL vierge et vérifie les invariants.
#
# Contre Supabase, « supabase db reset » suffit : ce script sert à valider les
# migrations sans dépendre d'un projet distant — en local, ou dans la CI.
set -euo pipefail

DSN="${1:-postgres://postgres@localhost:5432/postgres}"
RACINE="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

echo "→ schéma auth bouchonné (Supabase le fournit lui-même)"
psql -v ON_ERROR_STOP=1 -q -d "$DSN" -f "$RACINE/supabase/tests/stub_auth_local.sql"

for migration in "$RACINE"/supabase/migrations/*.sql; do
  echo "→ $(basename "$migration")"
  psql -v ON_ERROR_STOP=1 -q -d "$DSN" -f "$migration"
done

echo "→ invariants"
resultats="$(psql -q -t -A -d "$DSN" -f "$RACINE/supabase/tests/invariants.sql" \
  | grep -Ev '^(INSERT|UPDATE|CREATE)|^$')"
echo "$resultats"

echecs="$(printf '%s\n' "$resultats" | grep -c 'ÉCHEC' || true)"
reussites="$(printf '%s\n' "$resultats" | grep -c '^OK' || true)"
echo
echo "Bilan : $reussites OK, $echecs échec(s)"
[ "$echecs" -eq 0 ]
