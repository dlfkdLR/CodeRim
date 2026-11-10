#!/usr/bin/env bash
set -euo pipefail

base=${1:-}
windows=true
if [[ -n "$base" && ! "$base" =~ ^0+$ ]] &&
  git diff --quiet --no-renames "$base" HEAD -- \
    Windows Sources/CodeRim/Resources/ProviderScripts Tests/Fixtures .gitattributes \
    .github/workflows/windows.yml Scripts/ci_windows_changes.sh Scripts/ci_required_result.sh 2>/dev/null; then
  windows=false
fi
printf 'windows=%s\n' "$windows"
