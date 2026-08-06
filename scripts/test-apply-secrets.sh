#!/usr/bin/env bash
# Self-test for apply-secrets.sh.
#
# A guard script that stops working reports success forever, and nobody notices until a deploy ships a config with a
# literal placeholder as its password. These four cases are the ones that would actually go wrong.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
fail=0

check() {
  if [ "$2" = "$3" ]; then echo "  ok   $1"; else echo "  FAIL $1: expected [$3], got [$2]"; fail=1; fi
}

printf 'A=#{T_A}#\nB=#{T_B}#\n' > "$work/tpl"

# 1. Values with the characters a connection string really contains. sed would mangle every one of them. The dollar
# comes out doubled on purpose: compose reads this file and would otherwise interpolate everything after it away.
T_A='pw$with&slash/and\back' T_B='plain' ./scripts/apply-secrets.sh "$work/tpl" "$work/out" >/dev/null
check "special characters survive" "$(grep '^A=' "$work/out" | cut -d= -f2-)" 'pw$$with&slash/and\back'

# 2. The password hash is the value this actually went wrong on: three dollar-separated segments, all of them gone
# in the container, and the only symptom was a login that rejected the right password.
T_A='pbkdf2-sha256$210000$c2FsdA==$a2V5' T_B='plain' ./scripts/apply-secrets.sh "$work/tpl" "$work/hash" >/dev/null
check "hash keeps every segment" "$(grep '^A=' "$work/hash" | cut -d= -f2-)" 'pbkdf2-sha256$$210000$$c2FsdA==$$a2V5'

# 3. A missing value must abort and leave nothing behind — a half-written config is worse than none.
rm -f "$work/out2"
set +e
T_A='only-a' ./scripts/apply-secrets.sh "$work/tpl" "$work/out2" >/dev/null 2>&1
code=$?
set -e
check "missing secret aborts" "$code" "1"
check "missing secret writes no file" "$([ -f "$work/out2" ] && echo yes || echo no)" "no"

# 4. The result must not be world-readable: it holds the production password.
# GNU form first, BSD second. The other order silently passes on Linux: there `stat -f` means "file system status",
# succeeds, and prints a paragraph instead of a mode — so the fallback never runs and the comparison fails against
# text nobody expected. Exactly how this test failed on its first CI run.
mode=$(stat -c '%a' "$work/out" 2>/dev/null || stat -f '%Lp' "$work/out")
check "result is owner-only" "$mode" "600"

[ "$fail" = 0 ] && echo "apply-secrets self-test: passed" || { echo "apply-secrets self-test: FAILED"; exit 1; }
