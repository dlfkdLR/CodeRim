#!/bin/zsh
set -euo pipefail

script_dir=${0:A:h}
project_root=${script_dir:h}
source "${project_root}/Config/Release.env"

identity=${CODE_SIGN_IDENTITY:-}
cache_root=${CODERIM_BUILD_CACHE:-"$(getconf DARWIN_USER_CACHE_DIR)/dev.codexmeter.release"}
app_path=${1:-"${CODERIM_APP_PATH:-${cache_root}/${PRODUCT_NAME}.app}"}

if [[ -z "${identity}" ]]; then
  print -u2 "Set CODE_SIGN_IDENTITY to a Developer ID Application identity."
  exit 2
fi
if [[ ! -d "${app_path}" ]]; then
  print -u2 "App bundle not found: ${app_path}"
  exit 1
fi

sparkle_framework="${app_path}/Contents/Frameworks/Sparkle.framework"
claude_bridge="${app_path}/Contents/Helpers/CodeRimClaudeBridge"
if [[ ! -d "${sparkle_framework}" ]]; then
  print -u2 "Embedded Sparkle.framework not found: ${sparkle_framework}"
  exit 1
fi
if [[ ! -x "${claude_bridge}" ]]; then
  print -u2 "Claude limits helper not found: ${claude_bridge}"
  exit 1
fi

sparkle_version_root="${sparkle_framework}/Versions/Current"
sparkle_nested_code=(
  "${sparkle_version_root}/XPCServices/Downloader.xpc"
  "${sparkle_version_root}/XPCServices/Installer.xpc"
  "${sparkle_version_root}/Updater.app"
  "${sparkle_version_root}/Autoupdate"
  "${sparkle_framework}"
)
for candidate in "${sparkle_nested_code[@]}"; do
  codesign --force --sign "${identity}" --options runtime --timestamp \
    --preserve-metadata=identifier,entitlements,requirements "${candidate}"
done

codesign --force --sign "${identity}" --options runtime --timestamp "${claude_bridge}"
codesign --force --sign "${identity}" --options runtime --timestamp \
  "${app_path}/Contents/Helpers/CodeRimCLI"
# A real team signature uses an authorized macOS App Group; the local-only
# single-file exception is omitted from certificate-backed distribution.
signing_files=$(mktemp -d)
trap 'rm -rf -- "$signing_files"' EXIT
python3 - "$app_path" "$project_root" "$signing_files" <<'PY_SIGN'
from pathlib import Path
import os, plistlib, subprocess, sys
app, root, output = map(Path, sys.argv[1:])
details = subprocess.run(["codesign", "-d", "--verbose=4", str(app / "Contents/Helpers/CodeRimCLI")],
                         capture_output=True, text=True, check=True).stderr
team = next((line.split("=", 1)[1] for line in details.splitlines() if line.startswith("TeamIdentifier=")), "")
if not team or team == "not set":
    raise SystemExit("A valid Developer ID team is required for the widget App Group.")
host_info = app / "Contents/Info.plist"
host = plistlib.loads(host_info.read_bytes())
group = os.environ.get("CODERIM_APP_GROUP_ID") or team + "." + host["CFBundleIdentifier"]
if not group.startswith(team + "."):
    raise SystemExit("CODERIM_APP_GROUP_ID must begin with the signing Team ID followed by a dot.")
for info in [host_info, app / "Contents/PlugIns/CodeRimWidget.appex/Contents/Info.plist"]:
    data = plistlib.loads(info.read_bytes())
    data["CodexMeterSnapshotTransport"] = "app-group"
    data["CodexMeterAppGroup"] = group
    info.write_bytes(plistlib.dumps(data))
for name, template in [("app", root / "Config/CodeRim.entitlements"),
                       ("widget", root / "WidgetExtension/CodeRimWidget.entitlements")]:
    data = plistlib.loads(template.read_bytes())
    data["com.apple.security.application-groups"] = [group]
    (output / (name + ".entitlements")).write_bytes(plistlib.dumps(data))
PY_SIGN
codesign --force --sign "$identity" --options runtime --timestamp \
  --entitlements "$signing_files/widget.entitlements" \
  "$app_path/Contents/PlugIns/CodeRimWidget.appex"
codesign --force --sign "$identity" --options runtime --timestamp \
  --entitlements "$signing_files/app.entitlements" "$app_path"
codesign --verify --deep --strict --verbose=2 "${app_path}"
