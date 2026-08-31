#!/usr/bin/env bash
# Cherche une clé d'API ou un mot de passe écrit en dur dans le dépôt.
#
# Ce script commence par se vérifier lui-même sur un faux secret. Sans cela, un
# motif cassé afficherait « rien trouvé » et le contrôle passerait au vert sans
# avoir rien cherché — c'est exactement ce qui est arrivé à la première version,
# qui utilisait une anticipation négative que grep -E ne connaît pas, avec
# stderr redirigé vers /dev/null.
set -euo pipefail

# « "ApiKey": "valeur" » et « "Password": "valeur" » en JSON, et « Password= »
# dans une chaîne de connexion.
readonly MOTIF='("(ApiKey|Password)"[[:space:]]*:[[:space:]]*"[^"]+")|(Password[[:space:]]*=[^;"]+)'

# Ce qu'un gabarit a le droit de contenir.
readonly GABARITS='MOT_DE_PASSE|A_COMPLETER|A_RENSEIGNER|UTILISATEUR|SERVEUR_SQL|votre-cl|""'

chercher() {
  grep -rEn --include='*.json' --include='*.cs' --include='*.yml' --include='*.yaml' \
       --include='*.config' --include='*.sql' "$MOTIF" "$1" \
    | grep -v '/bin/\|/obj/\|/node_modules/' \
    | grep -vE "$GABARITS" \
    || true
}

# --- Le contrôle se contrôle ------------------------------------------------

bac="$(mktemp -d)"
trap 'rm -rf "$bac"' EXIT

printf '{ "ApiKey": "sk-une-vraie-cle-secrete" }\n' > "$bac/fuite.json"
printf '{ "Sage": "Server=X;Password=UnMotDePasseReel;" }\n' > "$bac/connexion.json"
printf '{ "ApiKey": "", "Password": "MOT_DE_PASSE" }\n'      > "$bac/gabarit.json"

attrapes="$(chercher "$bac" | wc -l)"
if [ "$attrapes" -ne 2 ]; then
  echo "::error::Le détecteur est cassé : $attrapes trouvaille(s) sur 2 secrets plantés."
  echo "Il aurait affiché « rien trouvé » sans avoir rien cherché."
  chercher "$bac"
  exit 2
fi

if chercher "$bac" | grep -q gabarit.json; then
  echo "::error::Le détecteur signale un gabarit comme un secret."
  exit 2
fi

echo "Détecteur vérifié : 2 secrets plantés sur 2 trouvés, gabarits ignorés."

# --- Le dépôt ---------------------------------------------------------------

trouvailles="$(chercher .)"
if [ -n "$trouvailles" ]; then
  echo "::error::Une clé ou un mot de passe semble écrit en dur."
  echo "$trouvailles"
  exit 1
fi

echo "Rien trouvé dans le dépôt."
