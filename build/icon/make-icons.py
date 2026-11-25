"""Regenerates every app icon file from build/icon/tandem.svg.

Writes:
  src/Tandem.App/Assets/Tandem.ico   Windows exe icon and window icon (16, 24, 32, 48, 64, 128, 256)
  build/icon/icon_1024.png           master raster
  build/Tandem.icns                  macOS bundle icon (16 … 1024, with @2x)

Each size is rendered from the vector source, not scaled down from a big bitmap, so small sizes stay sharp.
Needs: python3 -m pip install resvg-py Pillow
Usage: python3 build/icon/make-icons.py
"""

import io
from pathlib import Path

import resvg_py
from PIL import Image

ICON_DIR = Path(__file__).resolve().parent
REPO = ICON_DIR.parent.parent
SOURCE = ICON_DIR / "tandem.svg"
ICO = REPO / "src" / "Tandem.App" / "Assets" / "Tandem.ico"
PNG = ICON_DIR / "icon_1024.png"
ICNS = REPO / "build" / "Tandem.icns"

ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]
ICNS_SIZES = [16, 32, 64, 128, 256, 512, 1024]


def render(size: int) -> Image.Image:
    data = resvg_py.svg_to_bytes(svg_path=str(SOURCE), width=size, height=size)
    return Image.open(io.BytesIO(bytes(data))).convert("RGBA")


def main() -> None:
    renders = {size: render(size) for size in sorted(set(ICO_SIZES + ICNS_SIZES))}

    renders[1024].save(PNG)

    ICO.parent.mkdir(parents=True, exist_ok=True)
    largest = renders[ICO_SIZES[-1]]
    largest.save(ICO, format="ICO", sizes=[(s, s) for s in ICO_SIZES],
                 append_images=[renders[s] for s in ICO_SIZES[:-1]])

    renders[1024].save(ICNS, format="ICNS", append_images=[renders[s] for s in ICNS_SIZES[:-1]])

    for path in (ICO, PNG, ICNS):
        print(f"Wrote {path.relative_to(REPO)}")


if __name__ == "__main__":
    main()
