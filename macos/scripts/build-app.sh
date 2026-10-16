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
DMG=build/UsageDisplay.dmg
rm -rf "$APP" "$DMG" build/UsageDisplay.zip
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Frameworks"
cp "$BIN" "$APP/Contents/MacOS/UsageDisplay"
cp Resources/Info.plist "$APP/Contents/Info.plist"
# Already universal and signed by Sparkle; ditto keeps the framework's symlinks.
ditto "$(dirname "${SLICES[0]}")/Sparkle.framework" "$APP/Contents/Frameworks/Sparkle.framework"

# Ad-hoc (no Apple Developer account), so downloaders need
# System Settings → Privacy & Security → "Open Anyway" on first launch.
codesign --force --sign - "$APP"

STAGING=$(mktemp -d)
trap 'rm -rf "$STAGING"' EXIT
cp -R "$APP" "$STAGING/"
ln -s /Applications "$STAGING/Applications"
hdiutil create -volname "Usage Display" -srcfolder "$STAGING" -format UDZO -ov "$DMG"
echo "Built $APP and $DMG"
