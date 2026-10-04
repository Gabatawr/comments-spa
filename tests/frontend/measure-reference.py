#!/usr/bin/env python3
"""
measure-reference.py — reproducible measurements of docs/task/page1-X10.png.

Prints JSON with the numbers the QA design report relies on:
card bounds, header band rows/colours per depth, content left offsets,
quote bar colour. Run:  python3 tests/frontend/measure-reference.py
"""
import json
import sys
from collections import Counter
from pathlib import Path

from PIL import Image

REF = Path(__file__).resolve().parents[2] / "docs/task/page1-X10.png"


def main() -> int:
    im = Image.open(REF).convert("RGBA")
    px = im.load()
    w, h = im.size

    # opaque card bounds
    cols = [x for x in range(w) if any(px[x, y][3] > 250 for y in range(0, h, 10))]
    rows = [y for y in range(h) if any(px[x, y][3] > 250 for x in range(0, w, 10))]
    card = {"x0": min(cols), "x1": max(cols), "y0": min(rows), "y1": max(rows),
            "width": max(cols) - min(cols) + 1, "height": max(rows) - min(rows) + 1}

    # header bands = rows whose dominant colour near the card centre is not white
    bands = []
    suite = None
    for y in range(card["y0"], card["y1"] + 1):
        c = Counter(px[x, y] for x in range(card["x0"] + 60, card["x1"] - 20, 4)).most_common(1)[0][0]
        is_band = c[:3] != (255, 255, 255)
        if is_band and suite is None:
            suite = y
        if not is_band and suite is not None:
            mid = (suite + y - 1) // 2
            band_rgb = px[500, mid][:3]
            left = next((x for x in range(card["x0"], card["x1"]) if px[x, mid][:3] != (255, 255, 255)), None)
            right = next((x for x in range(card["x1"], card["x0"], -1) if px[x, mid][:3] != (255, 255, 255)), None)
            bands.append({
                "y0": suite,
                "y1": y - 1,
                "height": y - suite,
                "color": "#%02x%02x%02x" % band_rgb,
                "left": left,
                "right": right,
            })
            suite = None

    # leftmost content pixel inside each band -> avatar/content offset
    offsets = []
    for b in bands:
        y = (b["y0"] + b["y1"]) // 2
        first = None
        for x in range(b["left"], card["x1"]):
            if px[x, y][:3] != px[500, y][:3] and px[x, y][3] > 200:
                first = x
                break
        offsets.append(first)

    # quote bar colour #b3c2e1 anywhere
    bar = None
    for y in range(card["y0"], card["y1"]):
        xs = [x for x in range(card["x0"], card["x1"]) if px[x, y][:3] == (179, 194, 225) and px[x, y][3] > 200]
        if xs:
            bar = {"x": min(xs), "y": y, "color": "#b3c2e1", "sample_runs": len(xs)}
            break

    print(json.dumps({
        "image": {"width": w, "height": h},
        "card": card,
        "header_bands": bands,
        "band_content_left": offsets,
        "quote_bar": bar,
    }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
