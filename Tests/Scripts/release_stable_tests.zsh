#!/bin/zsh
set -euo pipefail

test_dir=${0:A:h}
project_root=${test_dir:h:h}
fixture_root=$(mktemp -d)
trap 'rm -rf "${fixture_root}"' EXIT

mkdir -p "${fixture_root}/Scripts"
cp "${project_root}/Scripts/release_stable.sh" \
  "${fixture_root}/Scripts/release_stable.sh"

{
  print '#!/bin/zsh'
  print 'set -euo pipefail'
  print '[[ "${CODERIM_REQUIRE_RELEASE_TAG:-}" == "1" ]]'
  print '[[ "${CODERIM_REQUIRE_PUBLIC_REPOSITORY:-}" == "1" ]]'
  print 'print context >> "${TRACE_PATH}"'
} > "${fixture_root}/Scripts/verify_release_context.sh"
{
  print '#!/bin/zsh'
  print 'set -euo pipefail'
  print 'print release >> "${TRACE_PATH}"'
} > "${fixture_root}/Scripts/release_unsigned.sh"
{
  print '#!/bin/zsh'
  print 'set -euo pipefail'
  print 'print appcast >> "${TRACE_PATH}"'
} > "${fixture_root}/Scripts/generate_appcast.sh"
chmod 755 "${fixture_root}/Scripts/"*.sh

trace_path="${fixture_root}/trace.txt"
TRACE_PATH="${trace_path}" "${fixture_root}/Scripts/release_stable.sh"

if [[ "$(<"${trace_path}")" != $'context\nrelease\nappcast' ]]; then
  print -u2 'Stable release steps did not run in order.'
  exit 1
fi

print 'Stable release tests passed.'
