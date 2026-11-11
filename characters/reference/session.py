"""Films a scripted session on the stage of a living pack: the agents' phases, the speed, a new peak, trouble, and the
user's cursor approaching, hovering, clicking and leaving, with the cursor drawn and a caption for each beat.

  python3 -m characters.reference.session --package build/claude-idle --pose cruise=build/claude-cruise ... \\
      --state think=build/claude-think ... --spec characters/specs/claude.json --out session.mp4 \\
      --captions session.txt

The script is SCRIPT below: times in seconds. Inputs follow spec/CHARACTERS.md, Behavior.
"""
import argparse
import json
import math
import subprocess
import time
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

from .hud import CJK_FONT, DIGITS_FONT, _font
from .motions.behavior import Inputs
from .motions.curves import Curve
from .stage import Stage

SCRIPT = {
    "hour": 1,
    "idle_before": 900.0,
    "requests": [(3.0, 17.0), (47.0, 47.4)],
    "phases": [(0.0, "idle"), (3.0, "generating"), (31.0, "tools"), (47.0, "generating"), (51.0, "idle")],
    "later": (82.5, 120.0),
    "speed": [(0, 0), (17, 0), (17.5, 7), (19, 12), (21, 20), (23, 34), (25, 55), (26.5, 90), (27, 128), (28.5, 115),
              (30, 80), (31, 60), (34, 0), (47.4, 0), (48, 12), (49, 20), (51, 18), (54, 0), (90, 0)],
    "events": [(27.0, "newPeak"), (38.0, "trouble")],
    "cursor": [(56.5, (1.4, 0.4)), (57.6, (0.62, 0.04)), (58.8, (0.6, 0.08)), (59.9, (-0.56, -0.04)),
               (60.8, (-0.6, 0.02)), (61.6, (0.05, 0.3)), (63.4, (0.1, 0.36)), (65.4, (0.02, 0.27)),
               (68.0, (0.06, 0.33)), (69.0, (0.0, 0.32)), (70.2, (0.03, 0.3)), (71.6, (-0.6, 0.12)),
               (73.0, (-1.6, -0.2))],
    "clicks": [69.0],
    "end": 90.0,
    "captions": [
        (0.0, "深夜，閒置很久：她在打瞌睡（rest）"),
        (3.0, "新的請求開始：醒來打招呼（greet）"),
        (5.6, "等待第一個 token：思考中（think）"),
        (17.0, "開始輸出：邊說邊隨速度換姿勢（work）"),
        (27.0, "創下新高：歡呼（cheer）"),
        (31.0, "等待工具結果：專注（focus）"),
        (38.0, "工具連續失敗：擔心（trouble）"),
        (40.6, "工具跑得久：拿起書專心看（focus）"),
        (47.0, "再輸出一小段（work）"),
        (51.0, "完成，等你回來（wait）"),
        (56.5, "游標靠近：轉頭、眼神跟著你"),
        (61.4, "停在她身上：微笑，然後比手勢"),
        (69.0, "點她一下：嚇一跳、笑出來（poke）"),
        (70.6, "游標離開：揮手道別"),
        (78.5, "沒人理她：抱著書等（wait）"),
        (82.5, "兩分鐘後，深夜：又睏了（rest）"),
    ],
}


def _at(keys, t, default=None):
    """The value of the last key at or before t."""
    out = default
    for start, value in keys:
        if t >= start:
            out = value
    return out


class Script:
    def __init__(self, doc):
        self.doc = doc
        self.speed = Curve(doc["speed"])
        cursor = doc["cursor"]
        self.cursor_x = Curve([(t, p[0]) for t, p in cursor])
        self.cursor_y = Curve([(t, p[1]) for t, p in cursor])
        self.cursor_span = (cursor[0][0], cursor[-1][0])
        self.last_end = None

    def phase(self, t):
        return _at(self.doc["phases"], t, "idle")

    def wait(self, t):
        for start, first in self.doc["requests"]:
            if start <= t < first:
                return t - start
        return None

    def idle(self, t):
        ended = [start for start, phase in self.doc["phases"] if phase == "idle" and start <= t]
        if self.phase(t) != "idle":
            return 0.0
        since = ended[-1] if ended else 0.0
        later_at, later_by = self.doc["later"]
        extra = later_by if t >= later_at else 0.0
        return (self.doc["idle_before"] if since == 0.0 else 0.0) + t - since + extra

    def events(self, t, dt):
        return tuple(name for at, name in self.doc["events"] if t - dt < at <= t)

    def click(self, t, dt):
        return any(t - dt < at <= t for at in self.doc["clicks"])

    def cursor(self, t):
        """The pointer relative to her face center in her heights, or None while off the script."""
        lo, hi = self.cursor_span
        if not lo <= t <= hi:
            return None
        return self.cursor_x(t), self.cursor_y(t)

    def caption(self, t):
        return _at(self.doc["captions"], t)


def arrow_texture(size):
    """A macOS-style arrow pointer: white with a dark rim, its tip at the top left."""
    s = size * 4
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pts = [(0.08, 0.04), (0.08, 0.78), (0.26, 0.62), (0.4, 0.92), (0.52, 0.86), (0.39, 0.57), (0.62, 0.57)]
    d.polygon([(x * s, y * s) for x, y in pts], fill=(255, 255, 255, 255), outline=(20, 20, 24, 255), width=size // 6)
    return np.asarray(img.resize((size, size), Image.LANCZOS))


def pill_texture(text, size, font_path, fill=(14, 16, 24, 190), color=(244, 246, 250, 255)):
    font = _font(font_path, size, b"Medium")
    box = font.getbbox(text)
    pad_x, pad_y = int(size * 0.8), int(size * 0.45)
    w, h = box[2] - box[0] + 2 * pad_x, box[3] - box[1] + 2 * pad_y
    img = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((0, 0, w - 1, h - 1), radius=h // 2, fill=fill, outline=(255, 255, 255, 46), width=1)
    d.text((pad_x - box[0], pad_y - box[1]), text, font=font, fill=color)
    return np.asarray(img)


class Overlay:
    """The cursor, a ripple where it clicks, the caption and a status chip of what the behavior chose."""

    def __init__(self, stage):
        self.stage = stage
        self.r = stage.renderer
        u = stage.unit
        self.arrow_size = int(58 * u)
        self.arrow = self.r.texture(arrow_texture(self.arrow_size), mipmaps=True)
        self.cache = {}
        self.clicked = None
        self.caption, self.caption_t = None, 0.0

    def _tex(self, key, make):
        if key not in self.cache:
            rgba = make()
            self.cache[key] = (self.r.texture(rgba, mipmaps=True), rgba.shape[1], rgba.shape[0])
        return self.cache[key]

    def draw(self, t, dt, pointer, click, caption, status):
        r, w, h, u = self.r, self.stage.w, self.stage.h, self.stage.unit
        if caption != self.caption:
            self.caption, self.caption_t = caption, 0.0
        self.caption_t += dt
        if caption:
            tex, tw, th = self._tex(("cap", caption), lambda: pill_texture(caption, int(34 * u), CJK_FONT))
            a = min(1.0, self.caption_t / 0.25)
            lift = (1 - a) * 10 * u
            r.image(tex, (w - tw) / 2, 0.035 * h - lift, tw, th, alpha=a)
        tex, tw, th = self._tex(("st", status), lambda: pill_texture(status, int(20 * u), DIGITS_FONT,
                                                                     fill=(14, 16, 24, 150),
                                                                     color=(214, 220, 232, 235)))
        r.image(tex, 0.025 * w, h - th - 0.025 * h, tw, th)
        if click and pointer is not None:
            self.clicked = (t, pointer)
        if self.clicked is not None:
            age = t - self.clicked[0]
            if age < 0.5:
                (x, y), k = self.clicked[1], age / 0.5
                rad = (14 + 46 * k) * u
                ring = [(x + rad * math.cos(a), y + rad * math.sin(a), 3.2 * u, 3.2 * u, 1.0, 0.86, 0.6,
                         0.9 * (1 - k), 0.5) for a in np.linspace(0, 2 * math.pi, 40, endpoint=False)]
                r.dots(np.array(ring, np.float32), additive=True)
        if pointer is not None:
            x, y = pointer
            s = self.arrow_size
            r.dots(np.array([(x + 0.2 * s, y + 0.38 * s, 0.32 * s, 0.4 * s, 0, 0, 0, 0.35, 1.0)], np.float32))
            r.image(self.arrow, x - 0.08 * s, y - 0.04 * s, s, s)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--package", required=True, help="the idle pose")
    ap.add_argument("--pose", action="append", default=[], help="band=package")
    ap.add_argument("--state", action="append", default=[], help="mood pose: state=package")
    ap.add_argument("--spec")
    ap.add_argument("--out", required=True)
    ap.add_argument("--captions", help="write the caption list (start time and text) here")
    ap.add_argument("--frames", help="save a frame every --frame-every seconds here")
    ap.add_argument("--frame-every", type=float, default=1.0)
    ap.add_argument("--fps", type=int, default=30)
    ap.add_argument("--size", default="1350x1350")
    ap.add_argument("--seconds", type=float)
    args = ap.parse_args(argv)
    spec = json.loads(Path(args.spec).read_text()) if args.spec else {}
    w, h = (int(v) for v in args.size.split("x"))
    stage = Stage(args.package, (w, h), [p.split("=", 1) for p in args.pose],
                  [s.split("=", 1) for s in args.state], spec=spec, living=True)
    script = Script(SCRIPT)
    overlay = Overlay(stage)
    seconds = args.seconds or SCRIPT["end"]
    ff = subprocess.Popen(["ffmpeg", "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgba", "-s", f"{w}x{h}",
                           "-r", str(args.fps), "-i", "-", "-c:v", "libx264", "-preset", "slow", "-crf", "17",
                           "-pix_fmt", "yuv420p", "-movflags", "+faststart", args.out], stdin=subprocess.PIPE)
    if args.frames:
        Path(args.frames).mkdir(parents=True, exist_ok=True)
    dt = 1 / args.fps
    log, cost, last = [], [], None
    home = None
    for n in range(int(round(seconds * args.fps))):
        t = (n + 1) * dt
        started = time.perf_counter()
        # The script's cursor is relative to where her face starts; it stays put on screen when she steps aside.
        home = home or stage.face_center()
        key = script.cursor(t)
        pointer = None if key is None else (home[0] + key[0] * stage.char_h, home[1] + key[1] * stage.char_h)
        inside = pointer is not None and 0 <= pointer[0] <= w and 0 <= pointer[1] <= h
        rel, over = None, False
        if inside:
            fx, fy = stage.face_center()
            rel = ((pointer[0] - fx) / stage.char_h, (pointer[1] - fy) / stage.char_h)
            over = stage.over(*pointer)
        click = script.click(t, dt)
        inputs = Inputs(phase=script.phase(t), wait=script.wait(t), idle=script.idle(t), hour=SCRIPT["hour"],
                        events=script.events(t, dt), cursor=rel, over=over, click=click and over)
        d = stage.director
        b = d.behavior

        def draw(renderer):
            status = (f"mood {b.mood} · pose {stage._name(stage.active)} · face {d.expr or '-'}"
                      f" · gaze {d.gaze_patch or '-'} · lv {stage.level}")
            overlay.draw(t, dt, pointer if inside else None, click, script.caption(t), status)

        frame = stage.step(dt, t, script.speed(t), inputs, overlay=draw)
        cost.append((time.perf_counter() - started) * 1000)
        ff.stdin.write(frame.tobytes())
        state = (b.mood, stage._name(stage.active), d.expr, d.gaze_patch, d.last_gesture if d.gesture else None)
        if state != last:
            log.append({"t": round(t, 2), "mood": state[0], "pose": state[1], "face": state[2], "gaze": state[3],
                        "action": state[4], "level": stage.level})
            last = state
        if args.frames and n % max(1, int(round(args.frame_every * args.fps))) == 0:
            Image.fromarray(frame[..., :3]).save(Path(args.frames) / f"t{t:06.2f}_{b.mood}.png")
    ff.stdin.close()
    ff.wait()
    if args.captions:
        lines = [f"{int(t // 60):d}:{t % 60:05.2f}  {text}" for t, text in SCRIPT["captions"]]
        Path(args.captions).write_text("\n".join(lines) + "\n", encoding="utf-8")
    Path(args.out).with_suffix(".log.json").write_text(json.dumps(log, ensure_ascii=False, indent=0), encoding="utf-8")
    print(json.dumps({"frames": n + 1, "seconds": seconds, "frame_ms_mean": round(float(np.mean(cost)), 2),
                      "moods": sorted({e["mood"] for e in log}), "poses": sorted({e["pose"] for e in log})}))


if __name__ == "__main__":
    main()
