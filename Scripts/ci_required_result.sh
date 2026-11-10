#!/usr/bin/env bash
set -euo pipefail

# Required checks need a result even when path selection skips a platform runner.
if [[ $# -eq 3 ]]; then
  case "$1:$2:$3" in
    success:true:success|success:false:success|success:false:skipped) exit 0 ;;
  esac
fi
printf '%s\n' 'Fast checks must pass, and required platform tests must succeed.' >&2
exit 1
