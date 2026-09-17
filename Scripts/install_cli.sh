#!/bin/zsh
set -euo pipefail

# In-app upgrades can retain the original bundle filename.
app_path=${1:-/Applications/CodeRim.app}
if [[ $# == 0 && ! -x "${app_path}/Contents/Helpers/CodeRimCLI" && -x /Applications/CodexMeter.app/Contents/Helpers/CodeRimCLI ]]; then
  app_path=/Applications/CodexMeter.app
fi
app_path=${app_path:A}
cli_dir=${CODERIM_CLI_BIN_DIR:-${CODEXMETER_CLI_BIN_DIR:-"${HOME}/.local/bin"}}
cli_dir=${cli_dir:A}
helper="${app_path}/Contents/Helpers/CodeRimCLI"
destination="${cli_dir}/coderim"

[[ -x "${helper}" ]] || { print -u2 "CLI helper not found: ${helper}"; exit 1; }
mkdir -p "${cli_dir}"
is_managed() {
  [[ "$1" == "${helper}" || "$1" == "${app_path}/Contents/Helpers/CodexMeterCLI" ||
     "$1" == "${app_path:h}/CodexMeter.app/Contents/Helpers/CodexMeterCLI" ||
     "$1" == "${app_path:h}/CodexMeter.app/Contents/Helpers/CodeRimCLI" ]]
}
install_link() {
  local destination=$1
  if [[ -L "${destination}" ]]; then
    local current=$(/usr/bin/readlink "${destination}")
    is_managed "${current}" || { print -u2 "A different CLI symlink already exists: ${destination}"; return 1; }
    [[ "${current}" == "${helper}" ]] && return 0
    local temporary="${cli_dir}/.coderim-cli-$$-${RANDOM}"
    ln -s "${helper}" "${temporary}"
    # -h replaces the symlink itself, never an unrelated destination directory.
    /bin/mv -fh "${temporary}" "${destination}"
  elif [[ -e "${destination}" ]]; then
    print -u2 "Refusing to replace an existing file: ${destination}"
    return 1
  else
    ln -s "${helper}" "${destination}"
  fi
}
install_link "${destination}"
legacy="${cli_dir}/codexmeter"
if [[ -L "${legacy}" ]] && is_managed "$(/usr/bin/readlink "${legacy}")"; then
  install_link "${legacy}"
fi
print "Installed ${destination}"
if [[ ":${PATH}:" != *":${cli_dir}:"* ]]; then
  print "Add ${cli_dir} to PATH to run coderim by name."
fi
