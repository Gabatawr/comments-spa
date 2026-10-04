#!/usr/bin/env python3
"""
compose-compare.py — build visual evidence for the cascade design comparison.

Inputs
  --reference   docs/task/page1-X10.png (914x824, card = x 32..881)
  --ours        raw cascade screenshot taken by cascade-compare.mjs
  --out-dir     directory for the generated artefacts
  --tag         timestamp tag used in file names
  --width       width our screenshot is scaled to (default 914: the whole canvas)
  --x --y       where to paste it on the reference-sized canvas (default 0 0)

Outputs (inside --out-dir)
  ours-<tag>.png            our screenshot scaled and pasted on a 914-wide
                            page-background canvas at the reference offset
  side-by-side-<tag>.png    reference | ours, same canvas size, labelled
  diff-<tag>.png            absolute per-pixel difference (autocontrast)
  diff-stats-<tag>.json     numeric diff summary

The reference PNG has a transparent page background; it is composited on the
DESIGN-v2 page colour #f5f7fb so that the two canvases are comparable.
"""
from __future__ import annotations

import argparse
import json
import os
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFont, ImageStat, ImageOps

PAGE_BG = (245, 247, 251)
REF_W, REF_H = 914, 824


def load_reference(path: str) -> Image.Image:
    im = Image.open(path).convert("RGBA")
    canvas = Image.new("RGBA", im.size, PAGE_BG + (255,))
    canvas.alpha_composite(im)
    return canvas.convert("RGB")


def load_ours(path: str, width: int) -> Image.Image:
    im = Image.open(path).convert("RGBA")
    if im.width == 0 or im.height == 0:
        raise SystemExit(f"empty screenshot: {path}")
    h = max(1, round(im.height * width / im.width))
    return im.resize((width, h), Image.LANCZOS)


def label(draw: ImageDraw.ImageDraw, xy, text: str) -> None:
    font = ImageFont.load_default()
    x, y = xy
    draw.rectangle([x - 4, y - 3, x + 8 * len(text) + 6, y + 12], fill=(20, 30, 50))
    draw.text((x, y), text, fill=(255, 255, 255), font=font)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--reference", required=True)
    ap.add_argument("--ours", required=True)
    ap.add_argument("--out-dir", required=True)
    ap.add_argument("--tag", required=True)
    ap.add_argument("--width", type=int, default=REF_W)
    ap.add_argument("--x", type=int, default=0)
    ap.add_argument("--y", type=int, default=0)
    args = ap.parse_args()

    os.makedirs(args.out_dir, exist_ok=True)
    ref = load_reference(args.reference)
    ours = load_ours(args.ours, args.width)

    canvas_h = max(REF_H, args.y + ours.height)
    ref_canvas = Image.new("RGB", (REF_W, canvas_h), PAGE_BG)
    ref_canvas.paste(ref, (0, 0))
    ours_canvas = Image.new("RGB", (REF_W, canvas_h), PAGE_BG)
    ours_canvas.paste(ours.convert("RGB"), (args.x, args.y))

    ours_path = os.path.join(args.out_dir, f"ours-{args.tag}.png")
    ours_canvas.save(ours_path)

    gap = 24
    header = 22
    sbs = Image.new("RGB", (REF_W * 2 + gap, canvas_h + header), (225, 228, 234))
    sbs.paste(ref_canvas, (0, header))
    sbs.paste(ours_canvas, (REF_W + gap, header))
    draw = ImageDraw.Draw(sbs)
    label(draw, (8, 5), f"REFERENCE docs/task/page1-X10.png {REF_W}x{REF_H}")
    label(draw, (REF_W + gap + 8, 5), f"OURS (scaled to {args.width}px, pasted {args.x},{args.y})")
    sbs_path = os.path.join(args.out_dir, f"side-by-side-{args.tag}.png")
    sbs.save(sbs_path)

    # ---- numeric diff on the aligned canvases ------------------------------
    diff = ImageChops.difference(ref_canvas, ours_canvas)
    gray = diff.convert("L")
    stat = ImageStat.Stat(gray)
    hist = gray.histogram()
    total = sum(hist)
    over32 = sum(hist[32:])
    changed = sum(hist[1:])

    # Fairer number: only the reference card bbox (x 32..881, y 24..783),
    # i.e. where the reference actually has content to compare against.
    box = (32, 24, 882, 784)
    d_card = gray.crop(box)
    s_card = ImageStat.Stat(d_card)
    h_card = d_card.histogram()
    t_card = sum(h_card)
    stats = {
        "reference": {"width": REF_W, "height": REF_H, "card_x": 32, "card_width": 850},
        "ours_raw": {"path": os.path.basename(args.ours), "scaled_to_width": args.width, "x": args.x, "y": args.y},
        "canvas": {"width": REF_W, "height": canvas_h},
        "diff": {
            "mean_abs": round(stat.mean[0], 3),
            "rms": round(stat.rms[0], 3),
            "max": gray.getextrema()[1],
            "pct_any_change": round(100.0 * changed / total, 2),
            "pct_over_32": round(100.0 * over32 / total, 2),
        },
        "diff_reference_card_region": {
            "box": list(box),
            "mean_abs": round(s_card.mean[0], 3),
            "pct_over_32": round(100.0 * sum(h_card[32:]) / t_card, 2),
        },
    }
    diff_vis = ImageOps.autocontrast(Image.merge("RGB", (gray, gray, gray)))
    diff_path = os.path.join(args.out_dir, f"diff-{args.tag}.png")
    diff_vis.save(diff_path)
    with open(os.path.join(args.out_dir, f"diff-stats-{args.tag}.json"), "w", encoding="utf-8") as fh:
        json.dump(stats, fh, ensure_ascii=False, indent=2)

    print(json.dumps({
        "ours_aligned": ours_path,
        "side_by_side": sbs_path,
        "diff": diff_path,
        **stats["diff"],
        "card_region": stats["diff_reference_card_region"],
    }, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
