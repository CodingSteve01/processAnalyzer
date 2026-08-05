#!/usr/bin/env bash
# Auto-format staged C# files with CSharpier and re-stage them.
# Used by lefthook pre-commit hook.
set -euo pipefail

files=("$@")

if [ ${#files[@]} -eq 0 ]; then
  echo "csharpier: no files to format"
  exit 0
fi

# Format the files in-place
csharpier format "${files[@]}"

# Re-stage the formatted files so the commit includes the fixes
git add "${files[@]}"
