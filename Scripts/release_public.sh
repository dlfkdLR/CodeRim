#!/bin/zsh
set -euo pipefail

script_dir=${0:A:h}
project_root=${script_dir:h}
source "${project_root}/Config/Release.env"

identity=${CODE_SIGN_IDENTITY:-}
team_id=${CODE_SIGN_TEAM_ID:-}
profile=${NOTARY_PROFILE:-}

CODERIM_REQUIRE_RELEASE_TAG=1 \
  CODERIM_REQUIRE_PUBLIC_REPOSITORY=1 \
  "${script_dir}/verify_release_context.sh"

if [[ -z "${identity}" || -z "${team_id}" || -z "${profile}" ]]; then
  print -u2 "Public release requires CODE_SIGN_IDENTITY, CODE_SIGN_TEAM_ID, and NOTARY_PROFILE."
  exit 2
fi
if [[ "${identity}" != "Developer ID Application:"* ]]; then
  print -u2 "CODE_SIGN_IDENTITY must name a Developer ID Application certificate."
  exit 2
fi

cache_root=${CODERIM_BUILD_CACHE:-"$(getconf DARWIN_USER_CACHE_DIR)/dev.coderim.release"}
app_path=${CODERIM_APP_PATH:-"${cache_root}/${PRODUCT_NAME}.app"}

"${script_dir}/build_release.sh"
"${script_dir}/sign_app.sh" "${app_path}"
"${script_dir}/notarize.sh" "${app_path}"
"${script_dir}/package_release.sh"
"${script_dir}/generate_appcast.sh"
CODERIM_REQUIRE_PUBLIC_RELEASE=1 \
  CODE_SIGN_TEAM_ID="${team_id}" \
  "${script_dir}/verify_release.sh" "${app_path}"
