#!/bin/zsh
set -euo pipefail

script_dir=${0:A:h}
project_root=${script_dir:h}
source "${project_root}/Config/Release.env"

tap_name=${CODERIM_LOCAL_TAP:-hechop/coderim-local}
release_version=${CODERIM_VERSION:-${MARKETING_VERSION}}
artifact_path="${project_root}/Artifacts/${PRODUCT_NAME}-${release_version}.zip"

if ! command -v brew >/dev/null 2>&1; then
  print -u2 "Homebrew is required: https://brew.sh"
  exit 2
fi
export HOMEBREW_NO_AUTO_UPDATE=1
if brew list --cask coderim >/dev/null 2>&1; then
  print -u2 "CodeRim is already installed. Remove it before installing this local candidate."
  exit 1
fi

if [[ ! -f "${artifact_path}" ]]; then
  "${script_dir}/release_unsigned.sh"
fi

developer_was_enabled=0
if brew developer 2>&1 | grep -q "Developer mode is enabled"; then
  developer_was_enabled=1
fi

restore_developer_mode() {
  if (( developer_was_enabled == 0 )); then
    brew developer off >/dev/null 2>&1 || true
  fi
}
trap restore_developer_mode EXIT

if ! tap_path=$(brew --repository "${tap_name}" 2>/dev/null); then
  brew tap-new --no-git "${tap_name}" >/dev/null
  tap_path=$(brew --repository "${tap_name}")
fi

mkdir -p "${tap_path}/Casks"
local_cask="${tap_path}/Casks/coderim.rb"
if [[ -f "${local_cask}" ]] && ! grep -q "Managed by CodeRim local installer" "${local_cask}"; then
  print -u2 "Refusing to replace an unmanaged Cask: ${local_cask}"
  exit 1
fi

artifact_sha=$(shasum -a 256 "${artifact_path}" | awk '{print $1}')
{
  print "# Managed by CodeRim local installer"
  print 'cask "coderim" do'
  print "  version \"${release_version}\""
  print "  sha256 \"${artifact_sha}\""
  print
  print "  url \"file://${artifact_path}\""
  print '  name "CodeRim"'
  print '  desc "Local Codex token usage in the menu bar"'
  print '  homepage "https://github.com/dlfkdLR/CodexMeter"'
  print
  print '  depends_on macos: :sonoma'
  print
  print '  app "CodeRim.app"'
  print
  print '  zap trash: ['
  print '    "~/Library/Application Support/CodexMeter",'
  print '    "~/Library/Preferences/dev.codexmeter.CodexMeter.plist",'
  print '  ]'
  print 'end'
} > "${local_cask}"

brew install --cask "${tap_name}/coderim"

print "Installed ${PRODUCT_NAME} ${release_version} with Homebrew."
print "Remove it with: brew uninstall --cask coderim"
print "Remove the local Tap with: brew untap ${tap_name}"
