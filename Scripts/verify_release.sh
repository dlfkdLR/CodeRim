#!/bin/zsh
set -euo pipefail

script_dir=${0:A:h}
project_root=${script_dir:h}
source "${project_root}/Config/Release.env"

release_version=${CODERIM_VERSION:-${MARKETING_VERSION}}
release_build=${CODERIM_BUILD_NUMBER:-${BUILD_NUMBER}}
release_bundle_id=${CODERIM_BUNDLE_ID:-${BUNDLE_IDENTIFIER}}
release_repository=${CODERIM_RELEASE_REPOSITORY:-${RELEASE_REPOSITORY}}
update_feed_branch=${CODERIM_UPDATE_FEED_BRANCH:-${UPDATE_FEED_BRANCH}}
expected_sparkle_feed_url="https://raw.githubusercontent.com/${release_repository}/${update_feed_branch}/appcast.xml"
require_public_release=${CODERIM_REQUIRE_PUBLIC_RELEASE:-0}
require_signed_release=${CODERIM_REQUIRE_SIGNED_RELEASE:-0}
require_unsigned_release=${CODERIM_REQUIRE_UNSIGNED_RELEASE:-0}
expected_team_id=${CODE_SIGN_TEAM_ID:-}
cache_root=${CODERIM_BUILD_CACHE:-"$(getconf DARWIN_USER_CACHE_DIR)/dev.coderim.release"}
app_path=${1:-"${CODERIM_APP_PATH:-${cache_root}/${PRODUCT_NAME}.app}"}
artifact_root="${project_root}/Artifacts"
zip_path="${artifact_root}/${PRODUCT_NAME}-${release_version}.zip"
dmg_path="${artifact_root}/${PRODUCT_NAME}-${release_version}.dmg"
checksums_path="${artifact_root}/SHA256SUMS.txt"
temporary_dir=$(mktemp -d)
mount_dir="${temporary_dir}/mounted"
mounted=0

if [[ "${require_unsigned_release}" == "1" \
    && ( "${require_signed_release}" == "1" || "${require_public_release}" == "1" ) ]]; then
  print -u2 "Unsigned verification cannot be combined with signed or public verification."
  exit 2
fi

cleanup() {
  if (( mounted )); then
    hdiutil detach -quiet "${mount_dir}" || true
  fi
  rm -rf "${temporary_dir}"
}
trap cleanup EXIT

verify_app() {
  local candidate=$1
  "${script_dir}/verify_license_notices.sh" "${candidate}"
  local binary_path="${candidate}/Contents/MacOS/${PRODUCT_NAME}"
  local info_plist="${candidate}/Contents/Info.plist"
  local sparkle_framework="${candidate}/Contents/Frameworks/Sparkle.framework"
  local sparkle_binary="${sparkle_framework}/Versions/Current/Sparkle"
  local claude_bridge="${candidate}/Contents/Helpers/CodeRimClaudeBridge"

  plutil -lint "${info_plist}"
  codesign --verify --deep --strict --verbose=2 "${candidate}"

  local actual_bundle_id actual_version actual_build actual_minimum_system ui_element
  local sparkle_feed_url sparkle_public_key sparkle_auto_checks sparkle_auto_update
  local sparkle_signed_feed sparkle_verify_before_extract sparkle_check_interval
  actual_bundle_id=$(/usr/libexec/PlistBuddy -c "Print :CFBundleIdentifier" "${info_plist}")
  actual_version=$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "${info_plist}")
  actual_build=$(/usr/libexec/PlistBuddy -c "Print :CFBundleVersion" "${info_plist}")
  actual_minimum_system=$(/usr/libexec/PlistBuddy -c "Print :LSMinimumSystemVersion" "${info_plist}")
  ui_element=$(/usr/libexec/PlistBuddy -c "Print :LSUIElement" "${info_plist}")
  sparkle_feed_url=$(/usr/libexec/PlistBuddy -c "Print :SUFeedURL" "${info_plist}")
  sparkle_public_key=$(/usr/libexec/PlistBuddy -c "Print :SUPublicEDKey" "${info_plist}")
  sparkle_auto_checks=$(/usr/libexec/PlistBuddy -c "Print :SUEnableAutomaticChecks" "${info_plist}")
  sparkle_auto_update=$(/usr/libexec/PlistBuddy -c "Print :SUAutomaticallyUpdate" "${info_plist}")
  sparkle_signed_feed=$(/usr/libexec/PlistBuddy -c "Print :SURequireSignedFeed" "${info_plist}")
  sparkle_verify_before_extract=$(/usr/libexec/PlistBuddy -c "Print :SUVerifyUpdateBeforeExtraction" "${info_plist}")
  sparkle_check_interval=$(/usr/libexec/PlistBuddy -c "Print :SUScheduledCheckInterval" "${info_plist}")
  if [[ "${actual_bundle_id}" != "${release_bundle_id}" \
      || "${actual_version}" != "${release_version}" \
      || "${actual_build}" != "${release_build}" \
      || "${actual_minimum_system}" != "${MINIMUM_SYSTEM_VERSION}" \
      || "${ui_element:l}" != "true" \
      || "${sparkle_feed_url}" != "${expected_sparkle_feed_url}" \
      || "${sparkle_public_key}" != "${SPARKLE_PUBLIC_KEY}" \
      || "${sparkle_auto_checks:l}" != "true" \
      || "${sparkle_auto_update:l}" != "true" \
      || "${sparkle_signed_feed:l}" != "true" \
      || "${sparkle_verify_before_extract:l}" != "true" \
      || "${sparkle_check_interval}" != "86400" ]]; then
    print -u2 "Release metadata does not match Config/Release.env: ${candidate}"
    exit 1
  fi

  if [[ ! -f "${sparkle_binary}" ]]; then
    print -u2 "Sparkle.framework is missing from the app bundle: ${candidate}"
    exit 1
  fi
  if [[ ! -x "${claude_bridge}" ]]; then
    print -u2 "Claude limits helper is missing from the app bundle: ${candidate}"
    exit 1
  fi
  codesign --verify --strict --verbose=2 "${claude_bridge}"
  local cli_helper="${candidate}/Contents/Helpers/CodeRimCLI"
  local widget="${candidate}/Contents/PlugIns/CodeRimWidget.appex"
  local widget_info="${widget}/Contents/Info.plist"
  local widget_binary="${widget}/Contents/MacOS/CodeRimWidget"
  local companion companion_architectures
  for companion in "${cli_helper}" "${widget_binary}"; do
    if [[ ! -x "${companion}" ]]; then
      print -u2 "Missing companion executable: ${companion}"
      exit 1
    fi
    companion_architectures=$(lipo -archs "${companion}")
    if [[ "${companion_architectures}" != *arm64* || "${companion_architectures}" != *x86_64* ]]; then
      print -u2 "Expected a Universal 2 companion: ${companion}"
      exit 1
    fi
  done
  codesign --verify --strict --verbose=2 "${cli_helper}"
  codesign --verify --strict --verbose=2 "${widget}"
  if [[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "${widget_info}")" != "${release_bundle_id}.widget" ||
        "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "${widget_info}")" != "${release_version}" ||
        "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "${widget_info}")" != "${release_build}" ]]; then
    print -u2 "Widget identity or version differs from its host."
    exit 1
  fi
  "${cli_helper}" --version
  local provider_logo
  for provider_logo in OpenAI Claude; do
    if [[ ! -s "${candidate}/Contents/Resources/ProviderLogos/${provider_logo}.svg" ]]; then
      print -u2 "Provider logo is missing from the app bundle: ${provider_logo}"
      exit 1
    fi
  done
  codesign --verify --deep --strict --verbose=2 "${sparkle_framework}"

  local sparkle_architectures
  sparkle_architectures=$(lipo -archs "${sparkle_binary}")
  if [[ "${sparkle_architectures}" != *arm64* || "${sparkle_architectures}" != *x86_64* ]]; then
    print -u2 "Expected a Universal 2 Sparkle framework, found: ${sparkle_architectures}"
    exit 1
  fi

  local architectures
  architectures=$(lipo -archs "${binary_path}")
  if [[ "${architectures}" != *arm64* || "${architectures}" != *x86_64* ]]; then
    print -u2 "Expected a Universal 2 binary, found: ${architectures}"
    exit 1
  fi
  local bridge_architectures
  bridge_architectures=$(lipo -archs "${claude_bridge}")
  if [[ "${bridge_architectures}" != *arm64* || "${bridge_architectures}" != *x86_64* ]]; then
    print -u2 "Expected a Universal 2 Claude limits helper, found: ${bridge_architectures}"
    exit 1
  fi

  local linked_libraries load_commands
  linked_libraries=$(otool -L "${binary_path}")
  load_commands=$(otool -l "${binary_path}")
  if [[ "${linked_libraries}" == *'/opt/homebrew'* \
      || "${linked_libraries}" == *'/usr/local'* ]]; then
    print -u2 "Release binary links to a developer-local runtime."
    exit 1
  fi
  if [[ "${linked_libraries}" != *'@rpath/Sparkle.framework/Versions/B/Sparkle'* ]]; then
    print -u2 "Release binary is not linked to the embedded Sparkle framework."
    exit 1
  fi
  if [[ "${load_commands}" != *'@executable_path/../Frameworks'* ]]; then
    print -u2 "Release binary cannot locate Contents/Frameworks at runtime."
    exit 1
  fi

  local signature_details
  signature_details=$(codesign -dv --verbose=4 "${candidate}" 2>&1)
  if [[ "${require_unsigned_release}" == "1" ]]; then
    if [[ "${signature_details}" != *"Signature=adhoc"* \
        || "${signature_details}" == *"Authority="* \
        || "${signature_details}" == *"runtime"* ]]; then
      print -u2 "Expected a non-Hardened Runtime ad-hoc signature: ${candidate}"
      exit 1
    fi
  elif [[ "${signature_details}" != *"runtime"* ]]; then
    print -u2 "Hardened Runtime is missing: ${candidate}"
    exit 1
  fi

  # Newer codesign emits no plist for an unentitled ad-hoc host.
  # Validate the exact host/widget transport policy instead of parsing empty stdin.
  python3 - "${candidate}" "${expected_team_id}" "${CODERIM_APP_GROUP_ID:-}" <<'PY_ENTITLEMENTS'
from pathlib import Path
import plistlib, subprocess, sys
app = Path(sys.argv[1])
widget = app / "Contents/PlugIns/CodeRimWidget.appex"
def entitlements(path):
    value = subprocess.run(["codesign", "-d", "--entitlements", ":-", str(path)],
                           check=True, capture_output=True).stdout
    return plistlib.loads(value) if value.strip() else {}
host_info = plistlib.loads((app / "Contents/Info.plist").read_bytes())
widget_info = plistlib.loads((widget / "Contents/Info.plist").read_bytes())
transport = host_info.get("CodexMeterSnapshotTransport")
assert widget_info.get("CodexMeterSnapshotTransport") == transport, "Companion transport differs"
if transport == "local-file":
    expected_host = {}
    expected_widget = {
        "com.apple.security.app-sandbox": True,
        "com.apple.security.temporary-exception.files.home-relative-path.read-only":
            ["/Library/Application Support/CodexMeter/Companion/snapshot.json"],
    }
elif transport == "app-group":
    team = sys.argv[2]
    group = sys.argv[3] or team + "." + host_info["CFBundleIdentifier"]
    assert team and group.startswith(team + "."), "An authorized team App Group is required"
    assert host_info.get("CodexMeterAppGroup") == widget_info.get("CodexMeterAppGroup") == group
    expected_host = {"com.apple.security.application-groups": [group]}
    expected_widget = {"com.apple.security.app-sandbox": True, **expected_host}
else:
    raise AssertionError("Unknown companion transport")
assert entitlements(app) == expected_host, "Unexpected host entitlements"
assert entitlements(widget) == expected_widget, "Unexpected widget entitlements"
print("Verified sealed companion access policy (" + transport + ")")
PY_ENTITLEMENTS

  if [[ "${require_signed_release}" == "1" || "${require_public_release}" == "1" ]]; then
    if [[ -z "${expected_team_id}" ]]; then
      print -u2 "CODE_SIGN_TEAM_ID is required for signed release verification."
      exit 2
    fi
    if [[ "${signature_details}" != *"Authority="* \
        || "${signature_details}" != *"TeamIdentifier=${expected_team_id}"* \
        || "${signature_details}" == *"Signature=adhoc"* ]]; then
      print -u2 "Expected a certificate-signed host app for team ${expected_team_id}: ${candidate}"
      exit 1
    fi

    local nested_code nested_signature_details
    local sparkle_version_root="${sparkle_framework}/Versions/Current"
    local signed_nested_code=(
      "${cli_helper}"
      "${widget}"
      "${sparkle_version_root}/XPCServices/Downloader.xpc"
      "${sparkle_version_root}/XPCServices/Installer.xpc"
      "${sparkle_version_root}/Updater.app"
      "${sparkle_version_root}/Autoupdate"
      "${sparkle_framework}"
    )
    for nested_code in "${signed_nested_code[@]}"; do
      nested_signature_details=$(codesign -dv --verbose=4 "${nested_code}" 2>&1)
      if [[ "${nested_signature_details}" != *"Authority="* \
          || "${nested_signature_details}" != *"TeamIdentifier=${expected_team_id}"* \
          || "${nested_signature_details}" == *"Signature=adhoc"* ]]; then
        print -u2 "Nested update code is not signed by team ${expected_team_id}: ${nested_code}"
        exit 1
      fi
    done
  fi

  if [[ "${require_public_release}" == "1" ]]; then
    if [[ "${signature_details}" != *"Authority=Developer ID Application:"* \
        || "${signature_details}" != *"TeamIdentifier=${expected_team_id}"* ]]; then
      print -u2 "Expected Developer ID signature for team ${expected_team_id}: ${candidate}"
      exit 1
    fi
    xcrun stapler validate "${candidate}"
    spctl --assess --type execute --verbose=4 "${candidate}"
  fi
  print "Verified ${candidate} (${architectures})"
}

verify_app "${app_path}"

if [[ ! -f "${zip_path}" || ! -f "${dmg_path}" \
    || ! -f "${zip_path}.sha256" || ! -f "${dmg_path}.sha256" \
    || ! -f "${checksums_path}" ]]; then
  print -u2 "Packaged ZIP, DMG, or checksum manifest is missing."
  exit 1
fi

(
  cd "${artifact_root}"
  shasum -a 256 -c "${zip_path:t}.sha256"
  shasum -a 256 -c "${dmg_path:t}.sha256"
  shasum -a 256 -c "${checksums_path:t}"
)

ditto -x -k "${zip_path}" "${temporary_dir}/zip"
verify_app "${temporary_dir}/zip/${PRODUCT_NAME}.app"

mkdir -p "${mount_dir}"
hdiutil attach -quiet -nobrowse -readonly -mountpoint "${mount_dir}" "${dmg_path}"
mounted=1
verify_app "${mount_dir}/${PRODUCT_NAME}.app"
hdiutil detach -quiet "${mount_dir}"
mounted=0

if [[ "${require_unsigned_release}" == "1" ]]; then
  print "Certificate-free release verified. Apple Developer ID trust and notarization are intentionally absent."
elif [[ "${require_public_release}" != "1" ]]; then
  print "Local candidate verified. Public release still requires Developer ID signing and notarization."
fi
