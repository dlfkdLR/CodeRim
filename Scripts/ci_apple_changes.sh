#!/usr/bin/env bash
set -euo pipefail

base=${1:-}
apple=true
# Deletions must remain visible when Apple files move into an excluded tree.
if [[ -n "$base" && ! "$base" =~ ^0+$ ]] &&
  git diff --quiet --no-renames "$base" HEAD -- . \
    ':!Windows' ':!docs' ':!Documentation' ':!website' ':!MobileRelay' \
    ':!*.md' ':!.github/workflows/windows*.yml' 2>/dev/null; then
  apple=false
fi
printf 'apple=%s\n' "$apple"
