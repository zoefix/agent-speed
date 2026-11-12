"""Films a package's variants one after another at rest, and a contact sheet of their heads.

  python3 -m characters.reference.preview --package build/claude-idle --variants-reel --out reel.mp4 \\
      --variants-sheet sheet.png

Each `Var.<id>` eases in over EASE seconds, holds HOLD, eases out and rests GAP, with its id in a corner; the frame is
the upper body. The sheet shows the head at rest, then with each variant fully in.
"""
import subprocess

import numpy as np
from PIL import Image, ImageDraw, ImageFont

from .render import Renderer

EASE, HOLD, GAP, LEAD = 0.15, 1.0, 0.35, 0.5
HEIGHT = 1080
BACKDROP = (238, 240, 244)
INK = (40, 44, 52)
FONTS = ("/System/Library/Fonts/SFNS.ttf", "C:/Windows/Fonts/segoeui.ttf",
         "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf")
SHEET_SCALE, SHEET_COLUMNS = 2.5, 6


def font(size):
    for path in FONTS:
        try:
            return ImageFont.truetype(path, size)
        except OSError:
            continue
    return ImageFont.load_default(size)


def schedule(ids):
    """Start time of each variant and the length of the reel."""
    starts = [LEAD + k * (2 * EASE + HOLD + GAP) for k in range(len(ids))]
    return list(zip(ids, starts)), LEAD + len(ids) * (2 * EASE + HOLD + GAP)


def weight(t, start):
    """The variant's value at time `t`: 0, eased up to 1 over EASE, held, eased back down."""
    u = t - start
    span = 2 * EASE + HOLD
    if u <= 0 or u >= span:
        return 0.0
    k = min(1.0, u / EASE, (span - u) / EASE)
    return k * k * (3 - 2 * k)


def _even(v):
    return int(round(v / 2)) * 2


def upper_body(doc):
    """Canvas box from above the head to below the waist, centered on the face."""
    lm = doc["landmarks"]
    fx0, _, fx1, _ = lm["face"]
    fw = lm["faceWidth"]
    head = [np.array(p["vertices"]).reshape(-1, 2) for p in doc["parts"]
            if p["tag"] in ("face", "front hair", "back hair", "headwear") and "@" not in p["name"]]
    top = min(v[:, 1].min() for v in head) - 0.15 * fw
    cx = (fx0 + fx1) / 2
    w, h = doc["canvas"]
    return max(0.0, cx - 1.7 * fw), max(0.0, top), min(float(w), cx + 1.7 * fw), min(float(h), lm["waist"][1] + 0.3 * fw)


def head(doc):
    fx0, fy0, fx1, fy1 = doc["landmarks"]["face"]
    fw, fh = fx1 - fx0, fy1 - fy0
    return fx0 - 0.45 * fw, fy0 - 0.35 * fh, fx1 + 0.45 * fw, fy1 + 0.2 * fh


def _label(frame, text, size):
    img = Image.fromarray(frame)
    d = ImageDraw.Draw(img)
    f = font(size)
    x, y = int(size * 0.8), img.height - int(size * 2.0)
    box = d.textbbox((x, y), text, font=f)
    pad = int(size * 0.35)
    d.rounded_rectangle((box[0] - pad, box[1] - pad, box[2] + pad, box[3] + pad), radius=pad,
                        fill=(255, 255, 255))
    d.text((x, y), text, font=f, fill=INK)
    return np.asarray(img)


def _values(rig, active):
    values = rig.defaults()
    values.update(active)
    return values


def film(doc, atlas, rig, out, fps=30):
    x0, y0, x1, y1 = upper_body(doc)
    scale = HEIGHT / (y1 - y0)
    w, h = _even((x1 - x0) * scale), _even(HEIGHT)
    offset = (-x0 * scale, -y0 * scale)
    renderer = Renderer((w, h), atlas, rig.parts)
    posed = rig.pose(rig.defaults())
    ids = [v["id"] for v in rig.variants]
    slots, seconds = schedule(ids)
    ffmpeg = subprocess.Popen(["ffmpeg", "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgb24",
                               "-s", f"{w}x{h}", "-r", str(fps), "-i", "-", "-c:v", "libx264", "-preset", "slow",
                               "-crf", "16", "-pix_fmt", "yuv420p", "-movflags", "+faststart", out],
                              stdin=subprocess.PIPE)
    background = tuple(c / 255 for c in BACKDROP) + (1.0,)
    span = 2 * EASE + HOLD
    for n in range(int(round(seconds * fps))):
        t = n / fps
        active = {f"Var.{vid}": weight(t, start) for vid, start in slots}
        renderer.begin(background)
        renderer.character(posed, scale, offset, rig.opacities(_values(rig, active)))
        frame = renderer.finish()[..., :3]
        current = next((k for k, (_, start) in enumerate(slots) if start - GAP / 2 <= t < start + span + GAP / 2), None)
        if current is not None:
            frame = _label(frame, f"{slots[current][0]}  {current + 1}/{len(slots)}", int(h * 0.024))
        ffmpeg.stdin.write(np.ascontiguousarray(frame).tobytes())
    ffmpeg.stdin.close()
    ffmpeg.wait()
    return {"frames": int(round(seconds * fps)), "fps": fps, "seconds": round(seconds, 2), "size": [w, h],
            "variants": ids}


def sheet(doc, atlas, rig, out):
    x0, y0, x1, y1 = head(doc)
    tw, th = _even((x1 - x0) * SHEET_SCALE), _even((y1 - y0) * SHEET_SCALE)
    renderer = Renderer((tw, th), atlas, rig.parts)
    posed = rig.pose(rig.defaults())
    offset = (-x0 * SHEET_SCALE, -y0 * SHEET_SCALE)
    background = tuple(c / 255 for c in BACKDROP) + (1.0,)
    tiles = [("rest", {})] + [(v["id"], {f"Var.{v['id']}": 1.0}) for v in rig.variants]
    caption = int(th * 0.09)
    f = font(int(caption * 0.62))
    cols = min(SHEET_COLUMNS, len(tiles))
    rows = -(-len(tiles) // cols)
    out_img = Image.new("RGB", (cols * tw, rows * (th + caption)), BACKDROP)
    d = ImageDraw.Draw(out_img)
    for k, (name, active) in enumerate(tiles):
        renderer.begin(background)
        renderer.character(posed, SHEET_SCALE, offset, rig.opacities(_values(rig, active)))
        x, y = (k % cols) * tw, (k // cols) * (th + caption)
        out_img.paste(Image.fromarray(renderer.finish()[..., :3]), (x, y))
        d.text((x + tw / 2, y + th + caption / 2), name, font=f, fill=INK, anchor="mm")
    out_img.save(out)
    return {"tiles": len(tiles), "tile": [tw, th]}
