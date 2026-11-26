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
# A Hardened Runtime host cannot load Sparkle's separately signed framework
# until sign_app.sh gives every nested component the same Developer ID. Keep
# the standalone ad-hoc build runnable; certificate-backed release workflows
# apply Hardened Runtime when they sign the finished bundle.
adhoc_hardened_runtime=${CODERIM_ADHOC_HARDENED_RUNTIME:-0}
sparkle_feed_url="https://raw.githubusercontent.com/${release_repository}/${update_feed_branch}/appcast.xml"
artifact_root="${project_root}/Artifacts"
cache_root=${CODERIM_BUILD_CACHE:-"$(getconf DARWIN_USER_CACHE_DIR)/dev.coderim.release"}
app_path=${CODERIM_APP_PATH:-"${cache_root}/${PRODUCT_NAME}.app"}
mkdir -p "${cache_root}"
# Retain the prepared dependencies so appcast generation uses the same pinned tools.
swift_scratch_path=${CODERIM_SWIFT_SCRATCH_PATH:-"${project_root}/.build/coderim-build"}

if [[ "${adhoc_hardened_runtime}" != "0" && "${adhoc_hardened_runtime}" != "1" ]]; then
  print -u2 "CODERIM_ADHOC_HARDENED_RUNTIME must be 0 or 1."
  exit 2
fi

"${script_dir}/verify_release_context.sh"
"${script_dir}/verify_update_policy.sh" "${project_root}/Config/Info.plist"

cd "${project_root}"
rust_inputs=""
staging_root=""
cleanup_build() {
  if [[ -n "${rust_inputs}" ]]; then rm -rf -- "${rust_inputs}"; fi
  if [[ -n "${staging_root}" ]]; then rm -rf -- "${staging_root}"; fi
}
trap cleanup_build EXIT
if [[ -z "${CODERIM_RUST_ACCOUNTING_ARCHIVE:-}" || -z "${CODERIM_RUST_WIDGET_ARCHIVE:-}" || -z "${CODERIM_MIGRATION_CLI:-}" ]]; then
  rust_inputs=$(mktemp -d "${TMPDIR:-/tmp}/coderim-rust-bundle.XXXXXXXX")
  python3 "${script_dir}/rust_migration/apple.py" prepare-bundle "${rust_inputs}"
  export CODERIM_RUST_ACCOUNTING_ARCHIVE=${CODERIM_RUST_ACCOUNTING_ARCHIVE:-"${rust_inputs}/libcoderim_accounting_bridge.a"}
  export CODERIM_RUST_WIDGET_ARCHIVE=${CODERIM_RUST_WIDGET_ARCHIVE:-"${rust_inputs}/libcoderim_widget.a"}
  export CODERIM_MIGRATION_CLI=${CODERIM_MIGRATION_CLI:-"${rust_inputs}/CodeRimCLI"}
fi
for rust_input in "${CODERIM_RUST_ACCOUNTING_ARCHIVE}" "${CODERIM_RUST_WIDGET_ARCHIVE}" "${CODERIM_MIGRATION_CLI}"; do
  lipo "${rust_input}" -verify_arch arm64
  lipo "${rust_input}" -verify_arch x86_64
done
# SwiftPM and the widget builder snapshot their native link inputs by content.
accounting_flags=(-Xswiftc -DCODERIM_RUST_ACCOUNTING -Xswiftc -I
  -Xswiftc "${project_root}/Rust/bridge/include" -Xlinker "${CODERIM_RUST_ACCOUNTING_ARCHIVE:A}")
for product in "${PRODUCT_NAME}" CodeRimCLI CodeRimClaudeBridge; do
  python3 "${script_dir}/prepare_swift_dependencies.py" --scratch-path "${swift_scratch_path}" \
    --run-swift build -c release --arch arm64 --arch x86_64 --product "${product}" -Xswiftc -warnings-as-errors "${accounting_flags[@]}"
done
product_directory=$(python3 "${script_dir}/prepare_swift_dependencies.py" --scratch-path "${swift_scratch_path}" \
  --show-bin-path -c release --arch arm64 --arch x86_64)
binary_path="${product_directory}/${PRODUCT_NAME}"
claude_bridge_path="${product_directory}/CodeRimClaudeBridge"
cli_path=${CODERIM_MIGRATION_CLI:A}
sparkle_framework="${product_directory}/Frameworks/Sparkle.framework"
if [[ ! -d "${sparkle_framework}" && -d "${product_directory}/Sparkle.framework" ]]; then
  sparkle_framework="${product_directory}/Sparkle.framework"
fi

"${script_dir}/build_widget.sh"
widget_path="${CODERIM_WIDGET_BUILD_PATH:-${project_root}/.build/widget-extension}/Build/Products/Release/CodeRimWidget.appex"
if [[ ! -x "${cli_path}" ]]; then
  print -u2 "CodeRim CLI was not produced at ${cli_path}"
  exit 1
fi

if [[ ! -f "${binary_path}" ]]; then
  print -u2 "Release executable was not produced at ${binary_path}"
  exit 1
fi
if [[ ! -f "${claude_bridge_path}" ]]; then
  print -u2 "Claude limits helper was not produced at ${claude_bridge_path}"
  exit 1
fi
if [[ ! -d "${sparkle_framework}" ]]; then
  print -u2 "Sparkle.framework was not produced at ${sparkle_framework}"
  exit 1
fi

if [[ "${app_path:t}" != "${PRODUCT_NAME}.app" ]]; then
  print -u2 "Refusing to replace unexpected app path: ${app_path}"
  exit 1
fi
bundle_destination=${app_path}
mkdir -p "${bundle_destination:h}"
staging_root=$(mktemp -d "${bundle_destination:h}/.coderim-build.XXXXXXXX")
app_path="${staging_root}/${PRODUCT_NAME}.app"
mkdir -p "${app_path}/Contents/MacOS" "${app_path}/Contents/Resources" \
  "${app_path}/Contents/Frameworks" "${app_path}/Contents/Helpers"
install -m 755 "${binary_path}" "${app_path}/Contents/MacOS/${PRODUCT_NAME}"
debug_bundle="${binary_path}.dSYM"
if [[ -n "${CODERIM_MIGRATION_SYMBOLS_DIR:-}" ]]; then
  mkdir "${CODERIM_MIGRATION_SYMBOLS_DIR}"
  debug_bundle="${CODERIM_MIGRATION_SYMBOLS_DIR}/${PRODUCT_NAME}.dSYM"
  if [[ -d "${binary_path}.dSYM" ]]; then
    ditto --norsrc --noextattr "${binary_path}.dSYM" "${debug_bundle}"
  fi
fi
if [[ ! -d "${debug_bundle}" ]]; then
  xcrun dsymutil "${binary_path}" -o "${debug_bundle}"
fi
# Strip the delivered copy while retaining the matching symbols in the build products.
packaged_binary="${app_path}/Contents/MacOS/${PRODUCT_NAME}"
if codesign -d "${packaged_binary}" 2>/dev/null; then
  codesign --remove-signature "${packaged_binary}"
fi
xcrun strip -x "${packaged_binary}"
packaged_uuids=$(xcrun dwarfdump --uuid "${packaged_binary}" | awk '{print $2, $3}' | sort)
symbol_uuids=$(xcrun dwarfdump --uuid "${debug_bundle}" | awk '{print $2, $3}' | sort)
if [[ -z "${packaged_uuids}" || "${packaged_uuids}" != "${symbol_uuids}" ]]; then
  print -u2 "Packaged executable and dSYM UUIDs must match."
  exit 1
fi
install -m 755 "${claude_bridge_path}" "${app_path}/Contents/Helpers/CodeRimClaudeBridge"
install -m 755 "${cli_path}" "${app_path}/Contents/Helpers/CodeRimCLI"
if [[ "${CODERIM_MIGRATION_RESTART:-1}" == "1" ]]; then
  # Both modes share the same signed binary and Rust standard library.
  ln -s CodeRimCLI "${app_path}/Contents/Helpers/CodeRimRestart"
fi
mkdir -p "${app_path}/Contents/PlugIns"
ditto --norsrc --noextattr "${widget_path}" "${app_path}/Contents/PlugIns/CodeRimWidget.appex"
install -m 644 "${project_root}/Assets/AppIcon.icns" "${app_path}/Contents/Resources/CodeRimOpenRim.icns"
install -m 644 "${project_root}/Config/Info.plist" "${app_path}/Contents/Info.plist"
for notice in LICENSE NOTICE; do
  install -m 644 "${project_root}/${notice}" "${app_path}/Contents/Resources/${notice}.txt"
done
install -m 644 "${project_root}/Rust/THIRD-PARTY-NOTICES.txt" "${app_path}/Contents/Resources/Rust-Notices.txt"
install -m 644 "${project_root}/Rust/cli/THIRD-PARTY-NOTICES.txt" "${app_path}/Contents/Resources/Rust-CLI-Notices.txt"
"${script_dir}/verify_license_notices.sh" "${app_path}"
provider_logo_source="${project_root}/Sources/CodeRim/Resources/ProviderLogos"
provider_logo_destination="${app_path}/Contents/Resources/ProviderLogos"
mkdir -p "${provider_logo_destination}"
for provider_logo in OpenAI Claude; do
  install -m 644 "${provider_logo_source}/${provider_logo}.svg" \
    "${provider_logo_destination}/${provider_logo}.svg"
done
ditto --norsrc --noextattr "${sparkle_framework}" \
  "${app_path}/Contents/Frameworks/Sparkle.framework"

# CodexBarCore ships the provider parsers/scripts as SwiftPM resources.
# Preserve every dependency resource bundle in packaged apps, including universal builds.
for resource_bundle in "${product_directory}/"*.bundle(N); do
  ditto --norsrc --noextattr "${resource_bundle}" "${app_path}/Contents/Resources/${resource_bundle:t}"
done

binary_load_commands=$(otool -l "${app_path}/Contents/MacOS/${PRODUCT_NAME}")
if [[ "${binary_load_commands}" != *'@executable_path/../Frameworks'* ]]; then
  install_name_tool -add_rpath '@executable_path/../Frameworks' \
    "${app_path}/Contents/MacOS/${PRODUCT_NAME}"
fi

/usr/libexec/PlistBuddy -c "Set :CFBundleDisplayName ${PRODUCT_NAME}" "${app_path}/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleExecutable ${PRODUCT_NAME}" "${app_path}/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleIdentifier ${release_bundle_id}" "${app_path}/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString ${release_version}" "${app_path}/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion ${release_build}" "${app_path}/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :LSMinimumSystemVersion ${MINIMUM_SYSTEM_VERSION}" "${app_path}/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :SUFeedURL ${sparkle_feed_url}" "${app_path}/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :SUPublicEDKey ${SPARKLE_PUBLIC_KEY}" "${app_path}/Contents/Info.plist"

/usr/libexec/PlistBuddy -c "Add :CodexMeterSnapshotTransport string local-file" "${app_path}/Contents/Info.plist"
xattr -cr "${app_path}"
codesign --force --sign - "${app_path}/Contents/Helpers/CodeRimClaudeBridge"
codesign --force --sign - "${app_path}/Contents/Helpers/CodeRimCLI"
codesign --force --sign - --entitlements "${project_root}/WidgetExtension/CodeRimWidget-Local.entitlements" \
  "${app_path}/Contents/PlugIns/CodeRimWidget.appex"
if [[ "${adhoc_hardened_runtime}" == "1" ]]; then
  codesign --force --sign - --options runtime "${app_path}"
elif [[ "${adhoc_hardened_runtime}" == "0" ]]; then
  codesign --force --sign - "${app_path}"
fi
codesign --verify --deep --strict "${app_path}"
python3 "${script_dir}/rust_migration/apple.py" replace-bundle "${app_path}" "${bundle_destination}"
print "Built ${bundle_destination}"
