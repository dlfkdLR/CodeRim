#!/bin/zsh
set -euo pipefail

script_dir=${0:A:h}
project_root=${script_dir:h}
app_path=${1:?Usage: verify_license_notices.sh /path/to/CodeRim.app}

# NOTICE is the canonical upstream attribution and full MIT grant. Compare
# exact bytes so an empty, truncated, or outdated bundled notice cannot ship.
for notice in LICENSE NOTICE; do
  source_path="${project_root}/${notice}"
  bundled_path="${app_path}/Contents/Resources/${notice}.txt"
  if [[ ! -s "${source_path}" || ! -s "${bundled_path}" ]] \
      || ! cmp -s "${source_path}" "${bundled_path}"; then
    print -u2 "Missing or mismatched ${notice}.txt in app bundle: ${app_path}"
    exit 1
  fi
done
print "Verified bundled license and Codenotch notices: ${app_path}"
