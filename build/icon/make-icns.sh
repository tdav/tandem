#!/usr/bin/env bash
# Regenerates build/Tandem.icns from build/icon/draw-icon.swift (needs Xcode command-line tools).
# Usage: build/icon/make-icns.sh
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
swift "$DIR/draw-icon.swift" "$DIR/icon_1024.png"
mkdir "$WORK/Tandem.iconset"
for s in 16 32 128 256 512; do
    sips -z $s $s "$DIR/icon_1024.png" --out "$WORK/Tandem.iconset/icon_${s}x${s}.png" >/dev/null
    sips -z $((s*2)) $((s*2)) "$DIR/icon_1024.png" --out "$WORK/Tandem.iconset/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$WORK/Tandem.iconset" -o "$DIR/../Tandem.icns"
echo "Wrote $(cd "$DIR/.." && pwd)/Tandem.icns"
