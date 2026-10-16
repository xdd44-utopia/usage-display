#!/bin/bash
# Writes the disk image's Finder layout to Resources/dmg-layout (committed; build-app.sh copies it into each image).
# Scripting Finder to save a layout is unreliable, so this writes the .DS_Store directly with dmgbuild's Python packages.
set -euo pipefail
cd "$(dirname "$0")/.."

# Finder resolves the background by disk name and path, so both must match build-app.sh.
MOUNT="/Volumes/Usage Monitor"
[ ! -e "$MOUNT" ] || { echo "Eject the mounted Usage Monitor disk image first" >&2; exit 1; }
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
python3 -m venv "$WORK/venv"
"$WORK/venv/bin/pip" install -q --disable-pip-version-check ds_store==1.3.1 mac_alias==2.2.2

mkdir -p "$WORK/src/.background"
touch "$WORK/src/.background/background.tiff"
hdiutil create -quiet -volname "Usage Monitor" -srcfolder "$WORK/src" -format UDRW "$WORK/layout.dmg"
trap 'hdiutil detach -quiet "$MOUNT" 2>/dev/null || hdiutil detach -quiet -force "$MOUNT" 2>/dev/null || true; rm -rf "$WORK"' EXIT
hdiutil attach -quiet -nobrowse -noautoopen -mountpoint "$MOUNT" "$WORK/layout.dmg"

# Positions are icon centers, matching the arrow drawn in the 560×360 background.
"$WORK/venv/bin/python" -I - "$MOUNT" Resources/dmg-layout <<'EOF'
import os, sys
from ds_store import DSStore
from mac_alias import Alias

mount, out = sys.argv[1:]
background = Alias.for_file(os.path.join(mount, ".background/background.tiff")).to_bytes()
if os.path.exists(out):
    os.remove(out)
with DSStore.open(out, "w+") as d:
    d["."]["vSrn"] = ("long", 1)
    d["."]["icvl"] = ("type", b"icnv")
    d["."]["bwsp"] = {
        "WindowBounds": "{{200, 500}, {560, 360}}",
        "ShowToolbar": False, "ShowStatusBar": False, "ShowPathbar": False, "ShowTabView": False,
        "ShowSidebar": False, "ContainerShowSidebar": False, "PreviewPaneVisibility": False,
    }
    d["."]["icvp"] = {
        "viewOptionsVersion": 1,
        "backgroundType": 2,
        "backgroundImageAlias": background,
        "backgroundColorRed": 1.0, "backgroundColorGreen": 1.0, "backgroundColorBlue": 1.0,
        "arrangeBy": "none",
        "iconSize": 128.0,
        "textSize": 12.0,
        "labelOnBottom": True,
        "showIconPreview": True,
        "showItemInfo": False,
        "gridSpacing": 100.0, "gridOffsetX": 0.0, "gridOffsetY": 0.0,
        "scrollPositionX": 0.0, "scrollPositionY": 0.0,
    }
    d["UsageMonitor.app"]["Iloc"] = (150, 170)
    d["Applications"]["Iloc"] = (410, 170)
EOF
echo "Saved Resources/dmg-layout"
