#!/bin/zsh
set -euo pipefail

test_dir=${0:A:h}
project_root=${test_dir:h:h}
fixture_root=$(mktemp -d)
trap 'rm -rf "${fixture_root}"' EXIT

mkdir -p "${fixture_root}/Config" "${fixture_root}/Scripts"
cp "${project_root}/Scripts/release_unsigned.sh" \
  "${fixture_root}/Scripts/release_unsigned.sh"
{
  print 'PRODUCT_NAME=CodeRim'
} > "${fixture_root}/Config/Release.env"

{
  print '#!/bin/zsh'
  print 'set -euo pipefail'
  print '[[ "${CODERIM_ADHOC_HARDENED_RUNTIME:-}" == "0" ]]'
  print '[[ -z "${CODE_SIGN_IDENTITY:-}" ]]'
  print '[[ -z "${CODE_SIGN_TEAM_ID:-}" ]]'
  print 'print build >> "${TRACE_PATH}"'
} > "${fixture_root}/Scripts/build_release.sh"
{
  print '#!/bin/zsh'
  print 'set -euo pipefail'
  print 'print package >> "${TRACE_PATH}"'
} > "${fixture_root}/Scripts/package_release.sh"
{
  print '#!/bin/zsh'
  print 'set -euo pipefail'
  print '[[ "${CODERIM_REQUIRE_UNSIGNED_RELEASE:-}" == "1" ]]'
  print '[[ "$1" == "${EXPECTED_APP_PATH}" ]]'
  print 'print verify >> "${TRACE_PATH}"'
} > "${fixture_root}/Scripts/verify_release.sh"
chmod 755 "${fixture_root}/Scripts/"*.sh

trace_path="${fixture_root}/trace.txt"
app_path="${fixture_root}/CodeRim.app"
TRACE_PATH="${trace_path}" \
  EXPECTED_APP_PATH="${app_path}" \
  CODERIM_APP_PATH="${app_path}" \
  CODE_SIGN_IDENTITY="must not leak" \
  CODE_SIGN_TEAM_ID="must not leak" \
  "${fixture_root}/Scripts/release_unsigned.sh"

if [[ "$(<"${trace_path}")" != $'build\npackage\nverify' ]]; then
  print -u2 'Certificate-free release steps did not run in order.'
  exit 1
fi

print 'Certificate-free release tests passed.'
