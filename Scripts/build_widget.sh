#!/bin/zsh
set -euo pipefail
script_dir=${0:A:h}
project_root=${script_dir:h}
source "${project_root}/Config/Release.env"
build_root=${CODERIM_WIDGET_BUILD_PATH:-"${project_root}/.build/widget-extension"}
rust_flags=()
if [[ -n "${CODERIM_RUST_WIDGET_ARCHIVE:-}" ]]; then
  rust_flags=('OTHER_SWIFT_FLAGS=$(inherited) -DCODERIM_RUST_WIDGET -I "$(CODERIM_WIDGET_HEADERS)"'
    'OTHER_LDFLAGS=$(inherited) "$(CODERIM_WIDGET_ARCHIVE)"'
    "CODERIM_WIDGET_HEADERS=${project_root}/Rust/bridge/include"
    "CODERIM_WIDGET_ARCHIVE=${CODERIM_RUST_WIDGET_ARCHIVE:A}")
fi
xcodebuild -quiet \
  -project "${project_root}/WidgetExtension/CodeRimWidget.xcodeproj" \
  -scheme CodeRimWidget -configuration Release \
  -destination 'generic/platform=macOS' -derivedDataPath "${build_root}" \
  "${rust_flags[@]}" CODE_SIGNING_ALLOWED=NO ARCHS='arm64 x86_64' ONLY_ACTIVE_ARCH=NO \
  PRODUCT_BUNDLE_IDENTIFIER="${CODERIM_BUNDLE_ID:-${BUNDLE_IDENTIFIER}}.widget" \
  MARKETING_VERSION="${CODERIM_VERSION:-${MARKETING_VERSION}}" \
  CURRENT_PROJECT_VERSION="${CODERIM_BUILD_NUMBER:-${BUILD_NUMBER}}" build
widget_path="${build_root}/Build/Products/Release/CodeRimWidget.appex"
[[ -x "${widget_path}/Contents/MacOS/CodeRimWidget" ]]
print "Built ${widget_path}"
