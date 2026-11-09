"""Renders a character beside a mock orb while the speed climbs from idle to redline and back.

  python3 -m characters.reference.preview --package characters/build/claude --out claude.mp4
  python3 -m characters.reference.preview --package characters/build/claude --rest-check claude.png
"""
import argparse
import json
import subprocess
import time
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

from ..rig import package
from ..rig.layers import load_reference
from ..rig.params import POS_X
from . import effects
from .motions import Director
from .physics import Physics
from .render import Renderer
from .rig import Rig

THRESHOLDS = [0.5, 8, 12, 16, 24, 32, 48, 68, 96, 136, 200, 280, 400]
TITLES = ["待機", "步行", "自行車", "電動自行車", "摩托車", "汽車", "跑車", "高鐵", "賽車", "磁浮列車", "噴射客機", "戰鬥機",
          "極音速飛機", "火箭"]
HEAT = [(0.0, (61, 220, 132)), (0.35, (255, 213, 74)), (0.7, (255, 138, 61)), (1.0, (255, 59, 59))]
TIMELINE = [(0, 0), (3, 0), (4, 14), (7, 22), (8, 60), (11, 75), (12, 150), (15, 170), (16, 330), (20, 420), (22, 60),
            (24, 10), (25, 0), (31, 0)]
REEL_GAP = 0.9
REEL_LABELS = {"wave": "揮手", "chin": "托腮思考", "peace": "比 YA", "fist": "握拳加油", "idea": "靈光一閃", "hip": "叉腰",
               "hairtuck": "撥頭髮", "present": "介紹手勢", "hug": "抱書", "raise": "舉書歡呼", "open": "翻書閱讀",
               "cheer": "雙手歡呼", "hooray": "揮手歡呼", "eureka": "讀到靈感", "ponder": "邊讀邊想", "cuddle": "抱書比 YA",
               "proud": "抱書叉腰", "hop": "跳一下", "jump": "大跳", "hops": "連跳", "jumpjoy": "跳起來歡呼",
               "spin": "轉圈", "step": "側步移動", "curtsy": "屈膝行禮", "tiptoe": "踮腳", "foottap": "踏腳打拍子",
               "sway": "擺臀跳舞", "run": "原地跑步", "stretch": "伸懶腰", "bow": "鞠躬", "peek": "偷看懸浮球",
               "yawn": "打哈欠", "hum": "哼歌", "nod": "點頭", "shake": "搖頭", "tilt": "歪頭", "lookaround": "東張西望",
               "giggle": "偷笑", "doubletake": "嚇一跳"}
REEL_EXTRA = ["cheer", "hooray", "eureka", "ponder", "cuddle", "proud", "hop", "hops", "jump", "jumpjoy", "spin",
              "step", "step", "curtsy", "tiptoe", "foottap", "sway", "run", "stretch", "bow", "peek", "yawn", "hum", "nod",
              "shake", "tilt", "lookaround", "giggle", "doubletake"]
DIGITS_FONT = "/System/Library/Fonts/SFNS.ttf"
TITLE_FONT = "/System/Library/Fonts/STHeiti Medium.ttc"


class LevelTracker:
    def __init__(self):
        self.level, self.candidate, self.since = 0, None, 0.0

    def update(self, v, dt):
        raw = self.level
        while raw < len(THRESHOLDS) and v >= THRESHOLDS[raw]:
            raw += 1
        while raw > 0 and v < 0.9 * THRESHOLDS[raw - 1]:
            raw -= 1
        if raw == self.level:
            self.candidate = None
            return self.level
        if self.candidate is None or (raw > self.level) != (self.candidate > self.level):
            self.since = 0.0
        self.candidate = raw
        self.since += dt
        if self.since >= (3.0 if raw == 0 else 0.8):
            self.level, self.candidate = raw, None
        return self.level


def speed_at(t):
    xs, ys = zip(*TIMELINE)
    return float(np.interp(t % xs[-1], xs, ys))


def heat_color(h):
    for (h0, c0), (h1, c1) in zip(HEAT, HEAT[1:]):
        if h <= h1:
            k = (h - h0) / (h1 - h0)
            return tuple(int(a + (b - a) * k) for a, b in zip(c0, c1))
    return HEAT[-1][1]


def orb_image(diameter, value, level, fonts):
    s = diameter
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.ellipse((4, 4, s - 4, s - 4), fill=(18, 21, 28, 236), outline=(255, 255, 255, 40), width=2)
    heat = min(1.0, value / THRESHOLDS[-1])
    color = heat_color(heat)
    inset = 22
    d.arc((inset, inset, s - inset, s - inset), 135, 405, fill=(255, 255, 255, 28), width=14)
    if heat > 0.002:
        d.arc((inset, inset, s - inset, s - inset), 135, 135 + 270 * heat, fill=color + (255,), width=14)
    text = f"{value:.0f}"
    big, small, title = fonts
    d.text((s / 2, s * 0.47), text, font=big, fill=(245, 247, 250, 255), anchor="mm")
    d.text((s / 2, s * 0.64), "tok/s", font=small, fill=(170, 178, 190, 255), anchor="mm")
    d.text((s / 2, s * 0.77), TITLES[level], font=title, fill=color + (255,), anchor="mm")
    return np.asarray(img)


def caption_image(text, font):
    w, h = int(font.size * (len(text) + 2)), int(font.size * 1.9)
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((0, 0, w - 1, h - 1), radius=h // 2, fill=(12, 15, 22, 170))
    d.text((w / 2, h / 2), text, font=font, fill=(240, 243, 248, 255), anchor="mm")
    return np.asarray(img)


def background(size):
    w, h = size
    y = np.linspace(0, 1, h)[:, None]
    x = np.linspace(0, 1, w)[None, :]
    top, bottom = np.array([58, 74, 102]), np.array([24, 30, 44])
    rgb = top * (1 - y[..., None]) + bottom * y[..., None]
    vignette = 1 - 0.35 * ((x - 0.5) ** 2 + (y - 0.45) ** 2)[..., None]
    rgba = np.concatenate([np.clip(rgb * vignette, 0, 255), np.full((h, w, 1), 255)], axis=2)
    return rgba.astype(np.uint8)


def petal_sprite(doc, atlas):
    part = next(p for p in doc["parts"] if p["tag"] == "headwear")
    uv = np.array(part["uv"]).reshape(-1, 2)
    x0, y0 = np.floor(uv.min(axis=0)).astype(int)
    x1 = int(np.ceil(uv[:, 0].max()))
    side = x1 - x0
    return np.ascontiguousarray(atlas[y0:y0 + side, x0:x0 + side])


def rest_check(rig, atlas, reference_path):
    renderer = Renderer(rig.canvas, atlas, rig.parts)
    renderer.begin((1, 1, 1, 1))
    renderer.character(rig.pose(rig.defaults()), 1.0, (0.0, 0.0), rig.opacities(rig.defaults()))
    frame = renderer.finish()[..., :3].astype(np.float32) / 255
    ref = load_reference(reference_path, rig.canvas)
    diff = np.abs(frame - ref).mean(axis=2) * 255
    return {"mean_abs_diff": round(float(diff.mean()), 3), "px_diff_gt_32_pct": round(float((diff > 32).mean() * 100), 3)}


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--package", required=True)
    ap.add_argument("--out")
    ap.add_argument("--rest-check")
    ap.add_argument("--frames")
    ap.add_argument("--fps", type=int, default=60)
    ap.add_argument("--seconds", type=float, default=TIMELINE[-1][0])
    ap.add_argument("--size", default="1280x760")
    ap.add_argument("--reel", action="store_true", help="steady cruise speed; play every gesture and expression in turn")
    ap.add_argument("--frame-every", type=float, default=0.5, help="seconds between saved frames")
    args = ap.parse_args(argv)

    doc, atlas = package.read(args.package)
    rig = Rig(doc)
    if args.rest_check:
        print(json.dumps(rest_check(rig, atlas, args.rest_check)))
        if not args.out:
            return

    w, h = (int(v) for v in args.size.split("x"))
    orb_d = int(h * 0.44)
    orb_c = (w * 0.26, h * 0.64)
    orb_bottom = orb_c[1] + orb_d / 2
    lm = doc["landmarks"]
    extent = np.concatenate([np.array(p["vertices"]).reshape(-1, 2) for p in doc["parts"]])
    left_x, top_y = extent.min(axis=0)
    scale = 1.6 * orb_d / (lm["feet"][1] - top_y)
    offset = (orb_c[0] + orb_d / 2 + 0.12 * orb_d - left_x * scale, orb_bottom - lm["feet"][1] * scale)

    renderer = Renderer((w, h), atlas, rig.parts)
    bg = renderer.texture(background((w, h)))
    glow = renderer.texture(effects.radial_texture())
    petal = renderer.texture(petal_sprite(doc, atlas), mipmaps=True)
    fonts = (ImageFont.truetype(DIGITS_FONT, int(orb_d * 0.27)), ImageFont.truetype(DIGITS_FONT, int(orb_d * 0.075)),
             ImageFont.truetype(TITLE_FONT, int(orb_d * 0.085)))

    def screen(pt):
        return pt[0] * scale + offset[0], pt[1] * scale + offset[1]

    xs = [screen((x, 0))[0] for x in (lm["face"][0] - 3 * lm["faceWidth"], lm["face"][2] + 3 * lm["faceWidth"])]
    petals = effects.Petals((xs[0], screen((0, top_y))[1], xs[1], orb_bottom), wind_sign=1.0)
    book = lm.get("objectSide") or "left"
    free = "right" if book == "left" else "left"
    director = Director(rig.params, rig.variants, arm_out=free, arm_book=book, orb=-1.0)
    shadow = renderer.texture(effects.shadow_texture())
    physics = Physics(doc["physics"], wind_sign=1.0)
    physics.reset(rig.defaults())
    tracker = LevelTracker()

    ffmpeg = None
    if args.out:
        ffmpeg = subprocess.Popen(["ffmpeg", "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgba",
                                   "-s", f"{w}x{h}", "-r", str(args.fps), "-i", "-", "-c:v", "libx264", "-preset",
                                   "slow", "-crf", "16", "-pix_fmt", "yuv420p", "-movflags", "+faststart", args.out],
                                  stdin=subprocess.PIPE)
    if args.frames:
        Path(args.frames).mkdir(parents=True, exist_ok=True)

    dt = 1 / args.fps
    reel, reel_at, caption = [], 1.0, None
    if args.reel:
        gestures = [v["id"] for v in rig.variants if v["kind"] == "arm"] + REEL_EXTRA
        reel = [g for g in gestures if director._gesture(g) is not None]
        args.seconds = reel_at + sum(director._gesture(g).duration + REEL_GAP for g in reel)
        caption_font = ImageFont.truetype(TITLE_FONT, int(h * 0.036))
    frames = int(args.seconds * args.fps)
    cost = []
    chest = screen((lm["waist"][0], lm["chin"] + 0.6 * (lm["waist"][1] - lm["chin"])))
    for n in range(frames):
        t = n * dt
        started = time.perf_counter()
        if args.reel:
            v, level = 40.0, 3
            director.set_level(level)
            director.next_gesture = float("inf")
            if director.gesture is not None:
                reel_at = t + REEL_GAP
            elif reel and t >= reel_at:
                gid = reel.pop(0)
                director._start_gesture(gid)
                if caption is not None:
                    caption.release()
                caption = renderer.texture(caption_image(REEL_LABELS.get(gid, gid), caption_font))
        else:
            v = speed_at(t)
            level = tracker.update(v, dt)
            director.set_level(level)
        values = director.update(dt)
        values.update(physics.step(dt, values))
        posed = rig.pose(values)
        for event in director.events:
            if event == "petals":
                petals.burst(chest[0] + values.get("PosX", 0.0) * POS_X * lm["faceWidth"] * scale, chest[1])
        director.events.clear()
        petals.update(dt, director.intensity)
        evaluated = time.perf_counter()

        renderer.begin((0, 0, 0, 1))
        renderer.image(bg, 0, 0, w, h)
        g = effects.glow_strength(director.intensity, t)
        if g > 0:
            size = 1.5 * orb_d
            renderer.image(glow, chest[0] - size / 2, chest[1] - size / 2, size, size, alpha=g,
                           premultiplied=True, additive=True)
        foot_x, foot_y = screen((lm["feet"][0] + values.get("PosX", 0.0) * POS_X * lm["faceWidth"], lm["feet"][1]))
        air = max(0.0, values.get("PosY", 0.0))
        sw, sh = orb_d * 0.55 * (1 - 0.45 * air), orb_d * 0.09 * (1 - 0.45 * air)
        renderer.image(shadow, foot_x - sw / 2, foot_y - sh * 0.55, sw, sh, alpha=0.55 * (1 - 0.6 * air))
        renderer.sprites(petal, petals.instances(front=False))
        renderer.character(posed, scale, offset, rig.opacities(values))
        renderer.sprites(petal, petals.instances(front=True))
        orb = renderer.texture(orb_image(orb_d, v, level, fonts))
        renderer.image(orb, orb_c[0] - orb_d / 2, orb_c[1] - orb_d / 2, orb_d, orb_d)
        orb.release()
        if caption is not None and director.gesture is not None:
            cw, ch = caption.size
            renderer.image(caption, orb_c[0] - cw / 2, h * 0.08, cw, ch, alpha=min(1.0, 4 * director.gesture["w"]))
        frame = renderer.finish()
        cost.append(((evaluated - started) * 1000, (time.perf_counter() - evaluated) * 1000))
        if ffmpeg:
            ffmpeg.stdin.write(frame.tobytes())
        if args.frames and n % max(1, int(round(args.frame_every * args.fps))) == 0:
            tag = f"_{director.last_gesture}" if director.gesture is not None else ""
            Image.fromarray(frame[..., :3]).save(Path(args.frames) / f"f{n:05d}_lv{level:02d}{tag}.png")
    if ffmpeg:
        ffmpeg.stdin.close()
        ffmpeg.wait()
    c = np.array(cost)
    print(json.dumps({"frames": frames, "fps": args.fps, "eval_ms_mean": round(float(c[:, 0].mean()), 2),
                      "eval_ms_p99": round(float(np.percentile(c[:, 0], 99)), 2),
                      "render_ms_mean": round(float(c[:, 1].mean()), 2)}))


if __name__ == "__main__":
    main()
