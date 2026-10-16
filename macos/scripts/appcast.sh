#!/bin/bash
# Writes the Sparkle feed for one release: appcast.sh <version> <dmg> <download-url> > appcast.xml
# Signs with SPARKLE_PRIVATE_KEY when set (CI), otherwise with the key in your login Keychain.
set -euo pipefail
cd "$(dirname "$0")/.."
VERSION=$1 DMG=$2 URL=$3

swift package resolve >/dev/null
SIGN_UPDATE=.build/artifacts/sparkle/Sparkle/bin/sign_update
if [ -n "${SPARKLE_PRIVATE_KEY:-}" ]; then
  ATTRIBUTES=$(printf '%s' "$SPARKLE_PRIVATE_KEY" | "$SIGN_UPDATE" --ed-key-file - "$DMG") || { echo "::error::sign_update failed" >&2; exit 1; }
else
  ATTRIBUTES=$("$SIGN_UPDATE" "$DMG") || { echo "::error::sign_update failed" >&2; exit 1; }
fi
MINIMUM=$(/usr/libexec/PlistBuddy -c "Print :LSMinimumSystemVersion" Resources/Info.plist)

cat <<EOF
<?xml version="1.0" encoding="utf-8"?>
<rss version="2.0" xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle">
  <channel>
    <title>Usage Display</title>
    <item>
      <title>Version $VERSION</title>
      <pubDate>$(LC_ALL=C date -u "+%a, %d %b %Y %H:%M:%S +0000")</pubDate>
      <sparkle:version>$VERSION</sparkle:version>
      <sparkle:shortVersionString>$VERSION</sparkle:shortVersionString>
      <sparkle:minimumSystemVersion>$MINIMUM</sparkle:minimumSystemVersion>
      <enclosure url="$URL" type="application/octet-stream" $ATTRIBUTES/>
    </item>
  </channel>
</rss>
EOF
