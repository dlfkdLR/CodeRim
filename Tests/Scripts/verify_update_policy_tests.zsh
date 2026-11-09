#!/bin/zsh
set -euo pipefail

test_dir=${0:A:h}
project_root=${test_dir:h:h}
fixture_root=$(mktemp -d)
trap 'rm -rf "${fixture_root}"' EXIT
fixture="${fixture_root}/Info.plist"
policy_script="${project_root}/Scripts/verify_update_policy.sh"
zsh "${policy_script}" "${project_root}/Config/Info.plist"

expect_rejection() {
  if zsh "${policy_script}" "${fixture}" > /dev/null 2>&1; then
    print -u2 'Insecure update configuration unexpectedly passed.'
    exit 1
  fi
}

for key in SURequireSignedFeed SUVerifyUpdateBeforeExtraction SUSignedFeedFailureExpirationInterval; do
  cp "${project_root}/Config/Info.plist" "${fixture}"
  /usr/libexec/PlistBuddy -c "Delete :${key}" "${fixture}"
  expect_rejection
done
for key in SURequireSignedFeed SUVerifyUpdateBeforeExtraction; do
  cp "${project_root}/Config/Info.plist" "${fixture}"
  /usr/libexec/PlistBuddy -c "Set :${key} false" "${fixture}"
  expect_rejection
done
for value in 1 1728000 -1; do
  cp "${project_root}/Config/Info.plist" "${fixture}"
  /usr/libexec/PlistBuddy -c "Set :SUSignedFeedFailureExpirationInterval ${value}" "${fixture}"
  expect_rejection
done
for type_value in 'string 0' 'bool false' 'real 0'; do
  cp "${project_root}/Config/Info.plist" "${fixture}"
  /usr/libexec/PlistBuddy -c 'Delete :SUSignedFeedFailureExpirationInterval' "${fixture}"
  /usr/libexec/PlistBuddy -c "Add :SUSignedFeedFailureExpirationInterval ${type_value}" "${fixture}"
  expect_rejection
done
print 'Update-policy tests passed (valid source and 11 rejected mutations).'
