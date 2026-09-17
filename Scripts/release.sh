#!/bin/zsh
set -euo pipefail

script_dir=${0:A:h}
project_root=${script_dir:h}
source "${project_root}/Config/Release.env"

identity=${CODE_SIGN_IDENTITY:-}
team_id=${CODE_SIGN_TEAM_ID:-}
if [[ -z "${identity}" || -z "${team_id}" ]]; then
  print -u2 "A runnable Sparkle build requires CODE_SIGN_IDENTITY and CODE_SIGN_TEAM_ID."
  print -u2 "Use an Apple Development identity for local signed candidates or release_public.sh for Developer ID."
  exit 2
fi

cache_root=${CODERIM_BUILD_CACHE:-"$(getconf DARWIN_USER_CACHE_DIR)/dev.codexmeter.release"}
app_path=${CODERIM_APP_PATH:-"${cache_root}/${PRODUCT_NAME}.app"}
"${script_dir}/build_release.sh"
CODE_SIGN_IDENTITY="${identity}" "${script_dir}/sign_app.sh" "${app_path}"
"${script_dir}/package_release.sh"
CODERIM_REQUIRE_SIGNED_RELEASE=1 \
  CODE_SIGN_TEAM_ID="${team_id}" \
  "${script_dir}/verify_release.sh" "${app_path}"
