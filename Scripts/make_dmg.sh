#!/bin/zsh
set -euo pipefail

script_dir=${0:A:h}
project_root=${script_dir:h}
source "${project_root}/Config/Release.env"

release_version=${CODERIM_VERSION:-${MARKETING_VERSION}}
cache_root=${CODERIM_BUILD_CACHE:-"$(getconf DARWIN_USER_CACHE_DIR)/dev.coderim.release"}
app_path=${1:-"${CODERIM_APP_PATH:-${cache_root}/${PRODUCT_NAME}.app}"}
dmg_path=${2:-"${project_root}/Artifacts/${PRODUCT_NAME}-${release_version}.dmg"}

if [[ ! -d "${app_path}" ]]; then
  print -u2 "App bundle not found: ${app_path}"
  exit 1
fi

"${script_dir}/verify_license_notices.sh" "${app_path}"

staging_dir=$(mktemp -d)
trap 'rm -rf "${staging_dir}"' EXIT
ditto --norsrc --noextattr "${app_path}" "${staging_dir}/${PRODUCT_NAME}.app"
ln -s /Applications "${staging_dir}/Applications"
rm -f "${dmg_path}"
hdiutil create -quiet -volname "${PRODUCT_NAME}" -srcfolder "${staging_dir}" \
  -ov -format UDZO "${dmg_path}"
print "Created ${dmg_path}"
