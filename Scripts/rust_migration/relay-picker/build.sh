#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/../../.." && pwd)"
rustc --edition=2024 --crate-type=cdylib --target wasm32-unknown-unknown \
  -C opt-level=z -C panic=abort -C strip=symbols -C link-arg=-zstack-size=65536 \
  "$root/MobileRelay/migration/quick_picker.rs" -o "$root/MobileRelay/migration/quick_picker.wasm"
