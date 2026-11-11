#!/usr/bin/env bash
set -euo pipefail

base=${1:-}
macos=true
ios=true
release=true
if [[ -n "$base" && ! "$base" =~ ^0+$ ]] &&
  changes=$(git diff --name-only --no-renames "$base" HEAD -- . 2>/dev/null); then
  macos=false
  ios=false
  release=false
  # No rename detection: the deleted source must still select its original platform.
  while IFS= read -r path; do
    case "$path" in
      ''|Windows/*|docs/*|Documentation/*|website/*|MobileRelay/*|*.md|.github/workflows/windows*.yml) ;;
      iOS/*) ios=true ;;
      Tests/CodeRimTests/*|Tests/CodeRimUsageCoreTests/*|Tests/CodeRimCompanionTests/*) macos=true ;;
      Sources/CodeRim/*|Sources/CodeRimUsageCore/*|Sources/CodeRimCLI/*|Sources/CodeRimClaudeBridge/*|Sources/ClaudeBridgeCore/*|Sources/CSQLite/*|Sources/CodeRimWidget/*|WidgetExtension/*|Assets/*)
        macos=true; release=true ;;
      *)
        # Shared inputs, build configuration and unknown paths fail open to every check.
        macos=true; ios=true; release=true ;;
    esac
  done <<< "$changes"
fi
printf 'macos=%s\nios=%s\nrelease=%s\n' "$macos" "$ios" "$release"
