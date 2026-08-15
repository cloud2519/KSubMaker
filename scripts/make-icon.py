# -*- coding: utf-8 -*-
"""Regenerates ``src/KSubMaker.App/Assets/app.ico``.

The icon is drawn, not designed in an editor, so it can be re-rendered at every size instead of
letting Windows downscale one bitmap. That matters because the design is size-adaptive: the full
motif — an eight-bar waveform over two subtitle lines, the Korean one green — turns to mush below
48 px, so small renditions drop to five thicker bars and a single green line.

The committed .ico is the artefact; this script only needs to run again when the design changes.
It needs Pillow (`tools\\python\\python.exe -m pip install Pillow`), which is deliberately not part
of the app runtime.

    tools\\python\\python.exe scripts\\make-icon.py
"""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw

REPO = Path(__file__).resolve().parent.parent
TARGET = REPO / "src" / "KSubMaker.App" / "Assets" / "app.ico"

# Windows asks for all of these: 16/24 taskbar+lists, 32/48 explorer, 64/256 tiles and zoom.
SIZES = [256, 64, 48, 32, 24, 16]

NAVY = (24, 34, 56, 255)
ACCENT = (61, 220, 151, 255)  # the produced Korean subtitle line
WHITE = (244, 246, 250, 255)
GREY = (148, 160, 184, 255)   # the source-language line


def render(s: int) -> Image.Image:
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([0, 0, s - 1, s - 1], radius=int(s * 0.22), fill=NAVY)

    small = s < 48

    # -- waveform, top half -------------------------------------------------
    heights = [0.45, 0.85, 1.0, 0.60, 0.80] if small else [0.30, 0.55, 0.85, 0.60, 1.0, 0.45, 0.75, 0.35]
    n = len(heights)
    span = s * 0.64
    x0 = (s - span) / 2
    bar_w = span / (n * (1.45 if small else 1.7))
    gap = (span - bar_w * n) / (n - 1)
    mid = s * 0.36
    max_h = s * 0.21

    for i, a in enumerate(heights):
        x = x0 + i * (bar_w + gap)
        h = max(max_h * a, 1.0)
        d.rounded_rectangle([x, mid - h, x + bar_w, mid + h], radius=int(bar_w / 2), fill=WHITE)

    # -- subtitle lines, bottom ---------------------------------------------
    line_h = max(s * 0.075, 2.0)
    if small:
        # One green line: at 16 px two lines are a smear, and the green line is the point.
        y = s * 0.72
        d.rounded_rectangle([s * 0.22, y, s * 0.78, y + line_h], radius=int(line_h / 2), fill=ACCENT)
    else:
        y = s * 0.66
        d.rounded_rectangle([s * 0.20, y, s * 0.80, y + line_h], radius=int(line_h / 2), fill=GREY)
        y2 = y + line_h * 1.9
        d.rounded_rectangle([s * 0.30, y2, s * 0.70, y2 + line_h], radius=int(line_h / 2), fill=ACCENT)

    return img


def main() -> None:
    TARGET.parent.mkdir(parents=True, exist_ok=True)

    largest = render(SIZES[0])
    largest.save(
        TARGET,
        format="ICO",
        sizes=[(s, s) for s in SIZES],
        append_images=[render(s) for s in SIZES[1:]],
    )
    print(f"wrote {TARGET} ({TARGET.stat().st_size:,} bytes, sizes {SIZES})")


if __name__ == "__main__":
    main()
