#!/bin/zsh
set -euo pipefail

script_dir=${0:A:h}
project_root=${script_dir:h}
source "${project_root}/Config/Release.env"

profile=${NOTARY_PROFILE:-}
cache_root=${CODERIM_BUILD_CACHE:-"$(getconf DARWIN_USER_CACHE_DIR)/dev.codexmeter.release"}
app_path=${1:-"${CODERIM_APP_PATH:-${cache_root}/${PRODUCT_NAME}.app}"}

if [[ -z "${profile}" ]]; then
  print -u2 "Set NOTARY_PROFILE to an xcrun notarytool Keychain profile."
  exit 2
fi
if [[ ! -d "${app_path}" ]]; then
  print -u2 "App bundle not found: ${app_path}"
  exit 1
fi

temporary_dir=$(mktemp -d)
trap 'rm -rf "${temporary_dir}"' EXIT
submission_zip="${temporary_dir}/${PRODUCT_NAME}.zip"
ditto -c -k --norsrc --noextattr --keepParent "${app_path}" "${submission_zip}"
xcrun notarytool submit "${submission_zip}" --keychain-profile "${profile}" --wait
xcrun stapler staple "${app_path}"
xcrun stapler validate "${app_path}"
