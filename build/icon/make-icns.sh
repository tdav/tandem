#!/usr/bin/env bash
# Regenerates build/Tandem.icns (and the other app icon files) from build/icon/tandem.svg.
# Kept for the old habit; the work is done by make-icons.py, which runs on any OS.
# Needs: python3 -m pip install resvg-py Pillow
# Usage: build/icon/make-icns.sh
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
python3 "$DIR/make-icons.py"
