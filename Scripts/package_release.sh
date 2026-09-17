#!/bin/zsh
set -euo pipefail

script_dir=${0:A:h}
project_root=${script_dir:h}
source "${project_root}/Config/Release.env"

release_version=${CODERIM_VERSION:-${MARKETING_VERSION}}
artifact_root="${project_root}/Artifacts"
cache_root=${CODERIM_BUILD_CACHE:-"$(getconf DARWIN_USER_CACHE_DIR)/dev.codexmeter.release"}
app_path=${CODERIM_APP_PATH:-"${cache_root}/${PRODUCT_NAME}.app"}
zip_path="${artifact_root}/${PRODUCT_NAME}-${release_version}.zip"
dmg_path="${artifact_root}/${PRODUCT_NAME}-${release_version}.dmg"
checksums_path="${artifact_root}/SHA256SUMS.txt"

if [[ ! -d "${app_path}" ]]; then
  print -u2 "App bundle not found: ${app_path}"
  exit 1
fi

# Check before removing existing artifacts or producing new downloads.
"${script_dir}/verify_license_notices.sh" "${app_path}"

mkdir -p "${artifact_root}"
rm -f "${zip_path}" "${zip_path}.sha256" "${dmg_path}" "${dmg_path}.sha256" \
  "${checksums_path}"
ditto -c -k --norsrc --noextattr --keepParent "${app_path}" "${zip_path}"
"${script_dir}/make_dmg.sh" "${app_path}" "${dmg_path}"

(
  cd "${artifact_root}"
  shasum -a 256 "${zip_path:t}" > "${zip_path:t}.sha256"
  shasum -a 256 "${dmg_path:t}" > "${dmg_path:t}.sha256"
  shasum -a 256 "${zip_path:t}" "${dmg_path:t}" > "${checksums_path:t}"
)
print "Packaged ${zip_path}"
print "Packaged ${dmg_path}"
