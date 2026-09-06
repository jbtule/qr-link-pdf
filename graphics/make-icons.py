#!/usr/bin/env python3
"""Regenerates the browser app's icon set from icon-orig-file.png.

Run from the repo root after changing the source image:

    python3 graphics/make-icons.py

Needs Pillow (`pip install pillow`).
"""
from pathlib import Path

from PIL import Image

root = Path(__file__).resolve().parent.parent
src = Image.open(root / "graphics" / "icon-orig-file.png").convert("RGBA")
out = root / "QrLinkPdf.Wasm" / "wwwroot"


def save(size: int, name: str) -> None:
    src.resize((size, size), Image.LANCZOS).save(out / name)


save(32, "favicon.png")
save(180, "apple-touch-icon.png")
save(192, "icon-192.png")
save(512, "icon-512.png")

# A multi-resolution .ico for browsers that still ask for one.
src.resize((256, 256), Image.LANCZOS).save(
    out / "favicon.ico", sizes=[(16, 16), (32, 32), (48, 48)]
)

print(f"wrote favicon.png, favicon.ico, apple-touch-icon.png, icon-192.png, icon-512.png to {out}")
