"""Films the character style: the character alone on the desktop with the speed, title, model and agents floating
around her, while the speed climbs from idle to redline and back.

Each speed band can have its own illustration (a separate package). When the band changes she turns side-on, the
illustration is swapped at the instant she is edge-on, and she turns back in the new pose, so the swap never shows.

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
    motion = spec.get("motion", {})
    if spec.get("accent"):
        args.vendor_color = ",".join(str(int(spec["accent"][i:i + 2], 16)) for i in (1, 3, 5))

    w, h = (int(v) for v in args.size.split("x"))
    unit = h / 1350
    doc, atlas = package.read(args.package)
    renderer = Renderer((w, h), atlas, Rig(doc).parts)
    base = Pose(args.package, renderer)
    poses = {"idle": base}
    for item in args.pose:
        band, path = item.split("=", 1)
        poses[band] = Pose(path, renderer, base=base)
    lm = base.lm
    extent = np.concatenate([np.array(p["vertices"]).reshape(-1, 2) for p in base.doc["parts"]])
    char_h = 0.72 * h
    scale0 = char_h / (lm["feet"][1] - extent[:, 1].min())
    feet_screen = (0.5 * w, 0.88 * h)
    fw = lm["faceWidth"]

    def placement(pose):
        s = scale0 * pose_scale(base.measures(), pose.measures())
        return s, (feet_screen[0] - pose.lm["feet"][0] * s, feet_screen[1] - pose.lm["feet"][1] * s)

    params = {}
    for pose in poses.values():
        params.update(pose.rig.params)
    bg = renderer.texture(desktop((w, h)))
    kind = motion.get("particles", "petals")
    try:
        petal = renderer.texture(petal_sprite(base.doc, base.atlas), mipmaps=True)
    except StopIteration:
        petal, kind = None, (kind if kind != "petals" else "sparkles")
    hud = Hud(renderer, unit)
    book = lm.get("objectSide") or "left"
    free = "right" if book == "left" else "left"
    director = Director(params, (), arm_out=free, arm_book=book, orb=1.0, celebrate=False, profile=motion)
    tracker = LevelTracker()
    switch = Switch()
    seated = bool(motion.get("seated"))
    fading, fade = None, 1.0
    active = base
    petals = effects.Particles(kind, (0.05 * w, 0.1 * h, 0.95 * w, 0.85 * h), accent=spec.get("accent"), wind_sign=1.0)
    vendor = tuple(int(v) for v in args.vendor_color.split(","))
    rng = random.Random(9)
    next_pulse, peak = 2.0, 0.0

    def pose_for(band):
        for b in reversed(BANDS[: BANDS.index(band) + 1]):
            if b in poses:
                return poses[b]
        return base

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
        v = speed_at(t)
        level = tracker.update(v, dt)
        director.set_level(level)
        values = director.update(dt)
        wanted = pose_for(band_of(level))
        if seated:
            # A character in a chair never turns: the poses crossfade instead.
            fade = min(1.0, fade + dt / 0.3)
            swapped = wanted if wanted is not active and fade >= 1.0 else None
            if swapped is not None:
                fading, fade = active, 0.0
        else:
            if wanted is not active:
                switch.request(wanted)
            swapped = switch.update(dt, values)
        if swapped is not None:
            active = swapped
            book_side = active.lm.get("objectSide") or book
            band = next(b for b, p in poses.items() if p is active)
            director.set_sides("right" if book_side == "left" else "left", book_side, pose_band=band)
            petals.burst(feet_screen[0] + values.get("PosX", 0.0) * POS_X * fw * scale0, feet_screen[1] - 0.55 * char_h)
        for pose in poses.values():
            out = pose.physics.step(dt, values)
            if pose is active:
                values.update(out)
        posed = active.rig.pose(values)
        scale, offset = placement(active)

        heat = min(1.0, v / THRESHOLDS[-1])
        if level > 0 and t >= next_pulse:
            hud.pulse(min(1.0, 0.4 + 0.2 * math.log10(1 + v)))
            next_pulse = t + rng.uniform(1.6, 4.0) / (1 + heat)
        if v > peak * 1.05 and v > 60:
            if v > peak * 1.25:
                hud.peak()
            peak = v
        hud.update(dt, t, v, TITLES[level], heat, director.intensity)

        dx = values.get("PosX", 0.0) * POS_X * fw * scale0
        dy = -values.get("PosY", 0.0) * POS_Y * fw * scale0
        fx, fy = feet_screen[0] + dx, feet_screen[1]
        ground = (fx, fy + 6 * unit, 0.27 * char_h, 0.27 * char_h * 0.17)
        waist = (fx + 0.01 * char_h, fy - 0.6 * char_h + dy, 0.3 * char_h, 0.05 * char_h)
        agents = AGENTS[: (0 if level == 0 else 1 if level < 5 else 2 if level < 9 else 3)]
        for event in director.events:
            if event == "petals":
                petals.burst(fx, fy - 0.55 * char_h + dy)
        director.events.clear()
        petals.update(dt, director.intensity)

        info = (args.model, f"{len(agents)} 個 agent · 今日 1.2M tokens", vendor)
        hud.orbit_bodies(dt, (fx, fy - 0.52 * char_h + 0.6 * dy), char_h)
        def particles(front):
            if petal is not None:
                renderer.sprites(petal, petals.sprites(front))
            renderer.dots(petals.dots(front), additive=petals.additive)
            for glyph, x, y, size, angle, alpha, rgb in petals.glyphs(front):
                color = tuple(int(255 * c) for c in rgb) + (255,)
                tex, tw, th = hud.tex.label(glyph, max(8, int(size * unit)), color, weight=b"Bold")
                renderer.sprites(tex, np.array([[x, y, tw, th, angle, alpha]], np.float32))

        renderer.begin((0, 0, 0, 1))
        renderer.image(bg, 0, 0, w, h)
        hud.draw_back(dt, ground, waist, agents)
        hud.draw_bodies(dt, False, *info)
        particles(False)
        if fading is not None and fade < 1.0:
            out_posed = fading.rig.pose(values)
            fs, fo = placement(fading)
            renderer.character(out_posed, fs, fo, [o * (1 - fade) for o in fading.rig.opacities(values)],
                               which=fading.which)
        alpha = fade if fading is not None else 1.0
        renderer.character(posed, scale, offset, [o * alpha for o in active.rig.opacities(values)], which=active.which)
        hud.draw_front(dt, ground, waist, agents)
        hud.draw_bodies(dt, True, *info)
        particles(True)
        sx, sy = hud.placed["speed"][:2]
        hud.draw_peak(dt, (min(max(sx, 0.18 * w), 0.82 * w), sy - 0.13 * char_h))
        frame = renderer.finish()
        cost.append((time.perf_counter() - started) * 1000)
        ff.stdin.write(frame.tobytes())
        if args.frames and n % max(1, int(round(args.frame_every * args.fps))) == 0:
            Image.fromarray(frame[..., :3]).save(Path(args.frames) / f"s{n:05d}_lv{level:02d}.png")
    ff.stdin.close()
    ff.wait()
    print(json.dumps({"frames": n + 1, "poses": sorted(poses), "frame_ms_mean": round(float(np.mean(cost)), 2)}))


if __name__ == "__main__":
    main()
