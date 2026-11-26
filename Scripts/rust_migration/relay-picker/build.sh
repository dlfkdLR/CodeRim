#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../../.."
rustc --edition=2024 --crate-type=cdylib --target wasm32-unknown-unknown \
  -C opt-level=z -C panic=abort -C strip=symbols -C link-arg=-zstack-size=65536 \
  MobileRelay/migration/quick_picker.rs -o MobileRelay/migration/quick_picker.wasm
