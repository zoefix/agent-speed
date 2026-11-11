"""Films the character style: the character alone on the desktop with the speed, title, model and agents floating
around her, while the speed climbs from idle to redline and back.

Each speed band can have its own illustration (a separate package), and so can the moods of a living pack (`--state
think=claude-think`). When the pose changes she turns side-on, the illustration is swapped at the instant she is
edge-on, and she turns back in the new pose, so the swap never shows. session.py films a scripted session through the
same stage, with the app's inputs.

  python3 -m characters.reference.stage --package claude --pose cruise=claude-cruise ... --out claude-stage.mp4
"""
import argparse
import json
import math
import random
import subprocess
import time
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

from ..rig import package
from ..rig.params import POS_X, POS_Y
from . import effects
from .hud import Hud
from .motions import Director, band_of
from .motions.behavior import KEEP
from .motions.curves import Curve
from .physics import Physics
from .preview import THRESHOLDS, TITLES, LevelTracker, petal_sprite, speed_at
from .render import Renderer
from .rig import Rig

AGENTS = [(217, 119, 87), (74, 158, 255), (16, 185, 129)]
BANDS = ["idle", "cruise", "boost", "overdrive", "redline"]


def desktop(size, seed=3):
    """A blurred wallpaper so the transparent window reads as floating on a desktop."""
    w, h = size
    rng = random.Random(seed)
    img = Image.new("RGB", (w, h), (22, 26, 40))
    blobs = Image.new("RGB", (w, h), (0, 0, 0))
    d = ImageDraw.Draw(blobs)
    for color in ((70, 60, 140), (30, 90, 140), (120, 60, 110), (40, 50, 90)):
        r = rng.uniform(0.25, 0.45) * w
        x, y = rng.uniform(0, w), rng.uniform(0, h)
        d.ellipse((x - r, y - r, x + r, y + r), fill=color)
    img = Image.blend(img, blobs.filter(ImageFilter.GaussianBlur(w * 0.12)), 0.75)
    return np.concatenate([np.asarray(img), np.full((h, w, 1), 255, np.uint8)], axis=2)


class Pose:
    def __init__(self, path, renderer, base=None):
        self.doc, atlas = package.read(path)
        self.rig = Rig(self.doc)
        self.lm = self.doc["landmarks"]
        self.which = renderer.add_character(atlas, self.rig.parts) if base is not None else 0
        self.physics = Physics(self.doc["physics"], wind_sign=1.0)
        self.physics.reset(self.rig.defaults())
        self.atlas = atlas

    def measures(self):
        """Face center to feet, face box diagonal, shoulders to hips (None in packs built without it)."""
        fx0, fy0, fx1, fy1 = self.lm["face"]
        return self.lm["feet"][1] - (fy0 + fy1) / 2, math.hypot(fx1 - fx0, fy1 - fy0), self.lm.get("torsoLength")


def pose_scale(base, pose):
    """How much a pose is scaled to match the idle pose: the middle of the three ratios of `Pose.measures`, so one
    that the pose throws off (tucked legs in a jump, a turned face) is outvoted; face to feet alone without a torso."""
    ratios = [b / p for b, p in zip(base, pose) if b and p]
    if len(ratios) == 3:
        return sorted(ratios)[1]
    return base[0] / pose[0]


class Switch:
    """Turn side-on, swap the illustration, turn back: Spin runs 0 -> 0.25 (edge-on), jumps to 0.75 (also edge-on)
    and runs on to 1, with a small hop and the skirt flaring."""

    OUT, IN = 0.2, 0.32

    def __init__(self):
        self.t, self.target = None, None
        self.hop = Curve([(0, 0), (0.08, -0.25), (0.18, 0.35), (0.36, 0.1), (0.46, -0.3), (0.52, 0)])
        self.lift = Curve([(0, 0), (0.12, 0), (0.3, 0.07), (0.46, 0), (0.52, 0)])
        self.flare = Curve([(0, 0), (0.2, 0.7), (0.4, 0.5), (0.7, 0)])

    def request(self, target):
        if self.t is None:
            self.t, self.target = 0.0, target

    def update(self, dt, values):
        """Returns the pose to switch to at the edge-on instant, else None."""
        if self.t is None:
            return None
        before = self.t
        self.t += dt
        swap = None
        if self.t < self.OUT:
            k = self.t / self.OUT
            values["Spin"] = 0.25 * k * k
        else:
            if before < self.OUT:
                swap, self.t = self.target, self.OUT
            k = min(1.0, (self.t - self.OUT) / self.IN)
            values["Spin"] = 0.75 + 0.25 * (1 - (1 - k) ** 3)
        values["Squash"] = values.get("Squash", 0.0) + self.hop(self.t)
        values["PosY"] = values.get("PosY", 0.0) + self.lift(self.t)
        values["SkirtFlare"] = max(values.get("SkirtFlare", 0.0), self.flare(self.t))
        if self.t >= self.OUT + self.IN:
            values["Spin"] = 0.0
            self.t = None
        return swap


class Stage:
    """The character style on a desktop: poses, director, readouts and particles, one frame per step.

    With `living`, the director gets the shown pose's variants and the pack's mood poses (`states`), and each step may
    pass the app's inputs (motions.behavior.Inputs): the mood then picks the pose and the readout pops a peak on the
    `newPeak` event instead of on its own."""

    def __init__(self, package_path, size=(1350, 1350), poses=(), states=(), spec=None, model="Claude Opus",
                 vendor_color=(217, 119, 87), living=False):
        spec = spec or {}
        self.motion = spec.get("motion", {})
        if spec.get("accent"):
            vendor_color = tuple(int(spec["accent"][i:i + 2], 16) for i in (1, 3, 5))
        self.w, self.h = w, h = size
        self.unit = h / 1350
        doc, atlas = package.read(package_path)
        self.renderer = Renderer((w, h), atlas, Rig(doc).parts)
        self.base = base = Pose(package_path, self.renderer)
        self.poses = {"idle": base}
        for band, path in poses:
            self.poses[band] = Pose(path, self.renderer, base=base)
        self.states = {state: Pose(path, self.renderer, base=base) for state, path in states}
        lm = base.lm
        extent = np.concatenate([np.array(p["vertices"]).reshape(-1, 2) for p in base.doc["parts"]])
        self.char_h = 0.72 * h
        self.scale0 = self.char_h / (lm["feet"][1] - extent[:, 1].min())
        self.feet_screen = (0.5 * w, 0.88 * h)
        self.fw = lm["faceWidth"]
        params = {}
        for pose in list(self.poses.values()) + list(self.states.values()):
            params.update(pose.rig.params)
        self.bg = self.renderer.texture(desktop((w, h)))
        kind = self.motion.get("particles", "petals")
        try:
            self.petal = self.renderer.texture(petal_sprite(base.doc, base.atlas), mipmaps=True)
        except StopIteration:
            self.petal, kind = None, (kind if kind != "petals" else "sparkles")
        self.hud = Hud(self.renderer, self.unit)
        self.book = lm.get("objectSide") or "left"
        free = "right" if self.book == "left" else "left"
        self.living = living
        variants = base.rig.variants if living else ()
        self.director = Director(params, variants, arm_out=free, arm_book=self.book, orb=1.0, celebrate=False,
                                 profile=self.motion, states=tuple(self.states) if living else None)
        self.tracker = LevelTracker()
        self.switch = Switch()
        self.seated = bool(self.motion.get("seated"))
        self.fading, self.fade = None, 1.0
        self.active = base
        self.started = False
        self.particles = effects.Particles(kind, (0.05 * w, 0.1 * h, 0.95 * w, 0.85 * h), accent=spec.get("accent"),
                                           wind_sign=1.0)
        self.vendor = tuple(vendor_color)
        self.model = model
        self.rng = random.Random(9)
        self.next_pulse, self.peak = 2.0, 0.0
        self.level, self.values = 0, {}

    def pose_for(self, band):
        for b in reversed(BANDS[: BANDS.index(band) + 1]):
            if b in self.poses:
                return self.poses[b]
        return self.base

    def wanted(self, band):
        """The mood pose when the mood names one the pack has, the shown pose for KEEP, else the band's."""
        mood = self.director.pose_wanted()
        if mood == KEEP:
            return self.active
        if mood in self.states:
            return self.states[mood]
        return self.pose_for(band)

    def placement(self, pose):
        s = self.scale0 * pose_scale(self.base.measures(), pose.measures())
        return s, (self.feet_screen[0] - pose.lm["feet"][0] * s, self.feet_screen[1] - pose.lm["feet"][1] * s)

    def _name(self, pose):
        return next((n for n, p in list(self.poses.items()) + list(self.states.items()) if p is pose), "idle")

    def _swapped(self, values, burst=True):
        book_side = self.active.lm.get("objectSide") or self.book
        free = "right" if book_side == "left" else "left"
        self.director.set_sides(free, book_side, pose_band=self._name(self.active),
                                variants=self.active.rig.variants if self.living else None)
        if burst:
                self.particles.burst(self.feet_screen[0] + values.get("PosX", 0.0) * POS_X * self.fw * self.scale0,
                                 self.feet_screen[1] - 0.55 * self.char_h)

    def face_center(self):
        """Her face center on screen, of the shown pose at rest, where she stands now."""
        s, (ox, oy) = self.placement(self.active)
        fx0, fy0, fx1, fy1 = self.active.lm["face"]
        dx = self.values.get("PosX", 0.0) * POS_X * self.fw * self.scale0
        return (fx0 + fx1) / 2 * s + ox + dx, (fy0 + fy1) / 2 * s + oy

    def over(self, x, y):
        """Whether a point is over her figure: the upright capsule of DESIGN 2a.6 (0.42 D wide, 0.9 D tall, centered
        0.04 D below the disc center), D from her height."""
        d = self.char_h / 0.86
        cx = self.feet_screen[0] + self.values.get("PosX", 0.0) * POS_X * self.fw * self.scale0
        cy = self.feet_screen[1] - 0.45 * d + 0.04 * d
        r = 0.21 * d
        half = 0.45 * d - r
        return math.hypot(x - cx, y - max(cy - half, min(cy + half, y))) <= r

    def step(self, dt, t, v, inputs=None, overlay=None):
        """One frame at speed v (tok/s); returns RGBA pixels. `overlay(renderer)` draws over everything."""
        r, w, h, unit = self.renderer, self.w, self.h, self.unit
        level = self.level = self.tracker.update(v, dt)
        self.director.set_level(level)
        values = self.director.update(dt, inputs)
        wanted = self.wanted(band_of(level))
        if not self.started:
            self.started = True
            if wanted is not self.active:
                self.active = wanted
                self._swapped(values, burst=False)
        if self.seated:
            self.fade = min(1.0, self.fade + dt / 0.3)
            swapped = wanted if wanted is not self.active and self.fade >= 1.0 else None
            if swapped is not None:
                self.fading, self.fade = self.active, 0.0
        else:
            if wanted is not self.active:
                self.switch.request(wanted)
            swapped = self.switch.update(dt, values)
        if swapped is not None:
            self.active = swapped
            self._swapped(values)
        for pose in list(self.poses.values()) + list(self.states.values()):
            out = pose.physics.step(dt, values)
            if pose is self.active:
                values.update(out)
        self.values = values
        posed = self.active.rig.pose(values)
        scale, offset = self.placement(self.active)

        heat = min(1.0, v / THRESHOLDS[-1])
        if level > 0 and t >= self.next_pulse:
            self.hud.pulse(min(1.0, 0.4 + 0.2 * math.log10(1 + v)))
            self.next_pulse = t + self.rng.uniform(1.6, 4.0) / (1 + heat)
        if inputs is not None:
            if "newPeak" in inputs.events:
                self.hud.peak()
        elif v > self.peak * 1.05 and v > 60:
            if v > self.peak * 1.25:
                self.hud.peak()
            self.peak = v
        self.hud.update(dt, t, v, TITLES[level], heat, self.director.intensity)

        char_h, fw, scale0 = self.char_h, self.fw, self.scale0
        dx = values.get("PosX", 0.0) * POS_X * fw * scale0
        dy = -values.get("PosY", 0.0) * POS_Y * fw * scale0
        fx, fy = self.feet_screen[0] + dx, self.feet_screen[1]
        ground = (fx, fy + 6 * unit, 0.27 * char_h, 0.27 * char_h * 0.17)
        waist = (fx + 0.01 * char_h, fy - 0.6 * char_h + dy, 0.3 * char_h, 0.05 * char_h)
        agents = AGENTS[: (0 if level == 0 else 1 if level < 5 else 2 if level < 9 else 3)]
        for event in self.director.events:
            if event == "petals":
                self.particles.burst(fx, fy - 0.55 * char_h + dy)
        self.director.events.clear()
        self.particles.update(dt, self.director.intensity)

        info = (self.model, f"{len(agents)} 個 agent · 今日 1.2M tokens", self.vendor)
        self.hud.orbit_bodies(dt, (fx, fy - 0.52 * char_h + 0.6 * dy), char_h)

        def particles(front):
            if self.petal is not None:
                r.sprites(self.petal, self.particles.sprites(front))
            r.dots(self.particles.dots(front), additive=self.particles.additive)
            for glyph, x, y, size, angle, alpha, rgb in self.particles.glyphs(front):
                color = tuple(int(255 * c) for c in rgb) + (255,)
                tex, tw, th = self.hud.tex.label(glyph, max(8, int(size * unit)), color, weight=b"Bold")
                r.sprites(tex, np.array([[x, y, tw, th, angle, alpha]], np.float32))

        r.begin((0, 0, 0, 1))
        r.image(self.bg, 0, 0, w, h)
        self.hud.draw_back(dt, ground, waist, agents)
        self.hud.draw_bodies(dt, False, *info)
        particles(False)
        if self.fading is not None and self.fade < 1.0:
            out_posed = self.fading.rig.pose(values)
            fs, fo = self.placement(self.fading)
            r.character(out_posed, fs, fo, [o * (1 - self.fade) for o in self.fading.rig.opacities(values)],
                        which=self.fading.which)
        alpha = self.fade if self.fading is not None else 1.0
        r.character(posed, scale, offset, [o * alpha for o in self.active.rig.opacities(values)],
                    which=self.active.which)
        self.hud.draw_front(dt, ground, waist, agents)
        self.hud.draw_bodies(dt, True, *info)
        particles(True)
        sx, sy = self.hud.placed["speed"][:2]
        self.hud.draw_peak(dt, (min(max(sx, 0.18 * w), 0.82 * w), sy - 0.13 * char_h))
        if overlay is not None:
            overlay(r)
        return r.finish()


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--package", required=True, help="the idle illustration")
    ap.add_argument("--pose", action="append", default=[], help="band=package for another band's illustration")
    ap.add_argument("--out", required=True)
    ap.add_argument("--frames")
    ap.add_argument("--frame-every", type=float, default=0.5)
    ap.add_argument("--fps", type=int, default=60)
    ap.add_argument("--seconds", type=float, default=62)
    ap.add_argument("--size", default="1350x1350")
    ap.add_argument("--model", default="Claude Opus")
    ap.add_argument("--vendor-color", default="217,119,87")
    ap.add_argument("--spec", help="the character's spec (characters/specs/<id>.json): motion profile and accent")
    args = ap.parse_args(argv)
    spec = json.loads(Path(args.spec).read_text()) if args.spec else {}
    w, h = (int(v) for v in args.size.split("x"))
    stage = Stage(args.package, (w, h), [item.split("=", 1) for item in args.pose], spec=spec, model=args.model,
                  vendor_color=tuple(int(v) for v in args.vendor_color.split(",")))
    ff = subprocess.Popen(["ffmpeg", "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgba", "-s", f"{w}x{h}",
                           "-r", str(args.fps), "-i", "-", "-c:v", "libx264", "-preset", "slow", "-crf", "16",
                           "-pix_fmt", "yuv420p", "-movflags", "+faststart", args.out], stdin=subprocess.PIPE)
    if args.frames:
        Path(args.frames).mkdir(parents=True, exist_ok=True)
    dt = 1 / args.fps
    cost = []
    for n in range(int(args.seconds * args.fps)):
        t = n * dt
        started = time.perf_counter()
        frame = stage.step(dt, t, speed_at(t))
        cost.append((time.perf_counter() - started) * 1000)
        ff.stdin.write(frame.tobytes())
        if args.frames and n % max(1, int(round(args.frame_every * args.fps))) == 0:
            Image.fromarray(frame[..., :3]).save(Path(args.frames) / f"s{n:05d}_lv{stage.level:02d}.png")
    ff.stdin.close()
    ff.wait()
    print(json.dumps({"frames": n + 1, "poses": sorted(stage.poses), "frame_ms_mean": round(float(np.mean(cost)), 2)}))


if __name__ == "__main__":
    main()
