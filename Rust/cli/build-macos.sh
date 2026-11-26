#!/bin/sh
# Build the optional companion executable outside the application bundle.
set -eu
if [ "$#" -ne 1 ]; then
    echo "Usage: $0 OUTPUT_EXECUTABLE" >&2
    exit 64
fi
output=$1
mkdir -p "$(dirname "$output")"
crate_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
target_dir=${CARGO_TARGET_DIR:-"$crate_dir/../target"}
export MACOSX_DEPLOYMENT_TARGET=${MACOSX_DEPLOYMENT_TARGET:-14.0}
for target in aarch64-apple-darwin x86_64-apple-darwin; do
    cargo build --locked --release --manifest-path "$crate_dir/Cargo.toml" \
        --features macos-companion --target-dir "$target_dir" --target "$target"
done
temporary=$(mktemp "$output.XXXXXX")
trap 'rm -f "$temporary"' EXIT
lipo -create "$target_dir/aarch64-apple-darwin/release/CodeRimCLI" \
    "$target_dir/x86_64-apple-darwin/release/CodeRimCLI" -output "$temporary"
lipo "$temporary" -verify_arch arm64
lipo "$temporary" -verify_arch x86_64
chmod 755 "$temporary"
mv "$temporary" "$output"
