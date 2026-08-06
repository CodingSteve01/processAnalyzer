#!/usr/bin/env bash
# Turns the committed .env.template into a usable .env by substituting #{TOKEN}# placeholders with environment
# variables — supplied by GitHub secrets in a deploy, or by the operator's shell for a manual rollout.
#
# The same idea as an established secrets-templating pattern, and for the same reason: the repository holds the
# SHAPE of the configuration, never a value. A connection string with a password in git is one clone away from being
# everywhere, and rotating it afterwards does not un-clone it.
#
# Fail-loud on purpose. A missing secret must abort the deploy, not produce a config that starts and quietly talks to
# nothing — or worse, falls back to a default that happens to point somewhere real.
#
# Usage:  SOURCE_CONNECTION_STRING=... POSTGRES_PASSWORD=... scripts/apply-secrets.sh [.env.template] [.env]
set -euo pipefail

TEMPLATE="${1:-.env.template}"
TARGET="${2:-.env}"

[ -f "$TEMPLATE" ] || { echo "apply-secrets: template not found: $TEMPLATE" >&2; exit 1; }

# Token names only — the values never reach a log, and the length is enough to tell "set" from "set to empty".
tokens=$(grep -oE '#\{[A-Z0-9_]+\}#' "$TEMPLATE" | sed 's/^#{//; s/}#$//' | sort -u)
if [ -z "$tokens" ]; then
  echo "apply-secrets: no tokens in $TEMPLATE — copying unchanged"
  cp "$TEMPLATE" "$TARGET"
  exit 0
fi

missing=""
content=$(cat "$TEMPLATE")

for name in $tokens; do
  # ${!name} would need bash 4 semantics that macOS's bash 3.2 does not give reliably for unset vars under set -u.
  value=$(printenv "$name" || true)
  if [ -z "$value" ]; then
    missing="$missing $name"
    echo "  $name: MISSING"
    continue
  fi

  echo "  $name: set (${#value} characters)"
  # The target is a docker compose env file, and compose interpolates $NAME and ${NAME} in it. A value containing a
  # dollar sign therefore arrives in the container shortened — the password hash is "pbkdf2-sha256$iterations$salt$key"
  # and lost three segments that way, which surfaces as "wrong password" and points at nothing. "$$" is compose's
  # escape for a literal dollar; doubling it here keeps the value intact on the other side.
  escaped=${value//\$/\$\$}
  # Bash parameter expansion, not sed and not python. sed would interpret the slashes, ampersands and backslashes a
  # connection string is full of; python would work but adds an interpreter this script must not need — it runs on
  # deploy hosts, and the .NET SDK image alone already has no python3. Quoting both sides makes the replacement
  # literal on each.
  content=${content//"#{$name}#"/"$escaped"}
done

if [ -n "$missing" ]; then
  echo "apply-secrets: aborting, no value for:$missing" >&2
  exit 1
fi

# A surviving token means the loop missed something; shipping it would start the container with a literal
# "#{...}#" as its password and the failure would surface as an authentication error nobody connects to this script.
if grep -qE '#\{[A-Z0-9_]+\}#' <<<"$content"; then
  echo "apply-secrets: aborting, unsubstituted tokens remain" >&2
  exit 1
fi

printf '%s\n' "$content" > "$TARGET"
chmod 600 "$TARGET"
echo "apply-secrets: wrote $TARGET"
