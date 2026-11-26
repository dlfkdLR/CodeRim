#!/bin/sh
# Build the optional companion executable outside the application bundle.
set -eu
if [ "$#" -lt 1 ] || [ "$#" -gt 2 ]; then
    echo "Usage: $0 OUTPUT_EXECUTABLE [CodeRimCLI|CodeRimClaudeBridge]" >&2
    exit 64
fi
output=$1
product=${2:-CodeRimCLI}
case "$product" in
    CodeRimCLI) profile=release ;;
    CodeRimClaudeBridge) profile=status-line ;;
    *) exit 64 ;;
esac
mkdir -p "$(dirname "$output")"
crate_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
target_dir=${CARGO_TARGET_DIR:-"$crate_dir/../target"}
export MACOSX_DEPLOYMENT_TARGET=${MACOSX_DEPLOYMENT_TARGET:-14.0}
for target in aarch64-apple-darwin x86_64-apple-darwin; do
    cargo build --locked --profile "$profile" --manifest-path "$crate_dir/Cargo.toml" \
        --features macos-companion --bin "$product" --target-dir "$target_dir" --target "$target"
done
temporary=$(mktemp "$output.XXXXXX")
trap 'rm -f "$temporary"' EXIT
lipo -create "$target_dir/aarch64-apple-darwin/$profile/$product" \
    "$target_dir/x86_64-apple-darwin/$profile/$product" -output "$temporary"
lipo "$temporary" -verify_arch arm64
lipo "$temporary" -verify_arch x86_64
if [ "$product" = CodeRimClaudeBridge ]; then
    xcrun strip -S -x "$temporary"
    codesign --force --sign - --identifier CodeRimClaudeBridge "$temporary"
fi
chmod 755 "$temporary"
mv "$temporary" "$output"
