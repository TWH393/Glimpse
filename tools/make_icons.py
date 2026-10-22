"""Package the selected PNG logo as a multi-resolution Windows icon.

Usage: python tools/make_icons.py path/to/selected-logo.png
Requires Pillow. The source artwork and its alpha channel are preserved.
"""
from pathlib import Path
import shutil
import sys

from PIL import Image


def main():
    source = Path(sys.argv[1]).resolve()
    assets = Path(__file__).resolve().parents[1] / "WpfApp1" / "Assets"
    assets.mkdir(parents=True, exist_ok=True)
    target = assets / "glimpse.png"
    if source != target.resolve():
        shutil.copyfile(source, target)
    with Image.open(target) as image:
        image.convert("RGBA").save(
            assets / "glimpse.ico",
            format="ICO",
            sizes=[(size, size) for size in (16, 20, 24, 32, 40, 48, 64, 128, 256)],
        )
    print(f"Saved logo: {target}")
    print(f"Saved Windows icon: {assets / 'glimpse.ico'}")


if __name__ == "__main__":
    main()
