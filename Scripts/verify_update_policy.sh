#!/bin/zsh
set -euo pipefail

# Shared by source-policy tests and every app inside the release ZIP/DMG.
info_plist=${1:?Usage: verify_update_policy.sh path/to/Info.plist}
for key in SURequireSignedFeed SUVerifyUpdateBeforeExtraction; do
  if [[ "$(/usr/bin/plutil -type "${key}" "${info_plist}")" != "bool" \
      || "$(/usr/libexec/PlistBuddy -c "Print :${key}" "${info_plist}")" != "true" ]]; then
    print -u2 "Required update signature policy is missing or disabled: ${key}"
    exit 1
  fi
done

interval_type=$(/usr/bin/plutil -type SUSignedFeedFailureExpirationInterval "${info_plist}")
interval=$(/usr/libexec/PlistBuddy -c 'Print :SUSignedFeedFailureExpirationInterval' "${info_plist}")
if [[ "${interval_type}" != "integer" || "${interval}" != "0" ]]; then
  print -u2 'Signed-feed failures must never expire: SUSignedFeedFailureExpirationInterval must be integer 0.'
  exit 1
fi
