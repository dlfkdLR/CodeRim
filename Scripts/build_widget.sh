#!/bin/zsh
set -euo pipefail
script_dir=${0:A:h}
project_root=${script_dir:h}
source "${project_root}/Config/Release.env"
build_root=${CODERIM_WIDGET_BUILD_PATH:-"${project_root}/.build/widget-extension"}
rust_inputs=()
if [[ -n "${CODERIM_RUST_WIDGET_ARCHIVE:-}" ]]; then
  rust_inputs=("${CODERIM_RUST_WIDGET_ARCHIVE:A}")
fi
widget_archive=$(python3 "${script_dir}/rust_migration/apple.py" widget-archive \
  "${build_root}/native-archives" "${rust_inputs[@]}")
# A supplied archive may predate the history reader. Both slices must provide
# the entry point before Swift emits a call from the universal extension.
history_flag='-DCODERIM_RUST_WIDGET_HISTORY'
for arch in arm64 x86_64; do
  if ! otool -arch "${arch}" -Sv "${widget_archive}" | awk '$NF == "_cr_widget_valid_history" { found=1 } END { exit !found }'; then
    history_flag=''
    break
  fi
done
rust_flags=('OTHER_SWIFT_FLAGS=$(inherited) -DCODERIM_RUST_WIDGET '"${history_flag}"' -I "$(CODERIM_WIDGET_HEADERS)"'
  'OTHER_LDFLAGS=$(inherited) "$(CODERIM_WIDGET_ARCHIVE)"'
  "CODERIM_WIDGET_HEADERS=${project_root}/Rust/bridge/include"
  "CODERIM_WIDGET_ARCHIVE=${widget_archive}")
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
