#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."

# One build per arch, merged: multi-arch in one call needs full Xcode.
SLICES=()
for ARCH in arm64 x86_64; do
  swift build -c release --triple "$ARCH-apple-macosx13.0"
  SLICES+=("$(swift build -c release --triple "$ARCH-apple-macosx13.0" --show-bin-path)/UsageDisplay")
done
mkdir -p build
BIN=build/UsageDisplay-universal
lipo -create -output "$BIN" "${SLICES[@]}"

APP=build/UsageDisplay.app
rm -rf "$APP" build/UsageDisplay.zip
mkdir -p "$APP/Contents/MacOS"
cp "$BIN" "$APP/Contents/MacOS/UsageDisplay"
cp Resources/Info.plist "$APP/Contents/Info.plist"
mkdir -p "$APP/Contents/Resources"
cp Resources/codex-logo.png Resources/anthropic-logo.png "$APP/Contents/Resources/"

# Ad-hoc (no Apple Developer account), so downloaders need
# System Settings → Privacy & Security → "Open Anyway" on first launch.
codesign --force --sign - "$APP"

ditto -c -k --keepParent "$APP" build/UsageDisplay.zip
echo "Built $APP and build/UsageDisplay.zip"
