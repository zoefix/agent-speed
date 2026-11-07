"""Speed effects: drifting petals carried by the wind and a warm glow at the top speeds."""
import math

import numpy as np

from .rng import Rng


class Petals:
    def __init__(self, area, wind_sign=1.0, seed=11):
        self.x0, self.y0, self.x1, self.y1 = area
        self.wind_sign = wind_sign
        self.rng = Rng(seed)
        self.items = []
        self.carry = 0.0

    def update(self, dt, intensity):
        rate = 34 * max(0.0, intensity - 0.15) ** 1.6
        self.carry += rate * dt
        while self.carry >= 1:
            self.carry -= 1
            w, h = self.x1 - self.x0, self.y1 - self.y0
            upwind = self.x0 - 0.15 * w if self.wind_sign > 0 else self.x1 + 0.15 * w
            self.items.append({
                "x": self.rng.uniform(self.x0, self.x1) if self.rng.random() < 0.6 else upwind,
                "y": self.rng.uniform(self.y0 + 0.05 * h, self.y0 + 0.75 * h),
                "vx": self.wind_sign * self.rng.uniform(120, 260) * (0.6 + intensity),
                "vy": self.rng.uniform(-40, 30),
                "rot": self.rng.uniform(0, 360), "spin": self.rng.uniform(-260, 260),
                "size": self.rng.uniform(16, 30), "age": 0.0, "life": self.rng.uniform(1.3, 2.4),
                "front": self.rng.random() < 0.55, "phase": self.rng.uniform(0, 6.28),
            })
        alive = []
        for p in self.items:
            p["age"] += dt
            if p["age"] >= p["life"]:
                continue
            p["x"] += p["vx"] * dt
            p["y"] += (p["vy"] + 18 * math.sin(p["age"] * 5 + p["phase"])) * dt
            p["rot"] += p["spin"] * dt
            alive.append(p)
        self.items = alive

    def burst(self, x, y, count=14, speed=260):
        """A puff of petals thrown out from (x, y), for spins and jumps."""
        for _ in range(count):
            a = self.rng.uniform(0, 2 * math.pi)
            v = self.rng.uniform(0.4, 1.0) * speed
            self.items.append({
                "x": x, "y": y, "vx": math.cos(a) * v, "vy": math.sin(a) * v * 0.6 - 60,
                "rot": self.rng.uniform(0, 360), "spin": self.rng.uniform(-300, 300),
                "size": self.rng.uniform(14, 26), "age": 0.0, "life": self.rng.uniform(0.9, 1.6),
                "front": self.rng.random() < 0.6, "phase": self.rng.uniform(0, 6.28),
            })

    def instances(self, front):
        out = []
        for p in self.items:
            if p["front"] != front:
                continue
            k = p["age"] / p["life"]
            alpha = min(1.0, k / 0.15) * min(1.0, (1 - k) / 0.3)
            squash = 0.55 + 0.45 * abs(math.cos(math.radians(p["rot"] * 1.7)))
            out.append((p["x"], p["y"], p["size"], p["size"] * squash, math.radians(p["rot"]), alpha))
        return np.array(out, np.float32).reshape(-1, 6)


# A character's ambient particles (spec/CHARACTERS.md, Particles), besides Claude's atlas petals:
# look, emission (base, gain, floor, power: base + gain * max(0, s - floor) ** power; inverse: base * (1 - s) ** 2),
# origin, vx, vy (points/s), wind (vx per unit of wind), wobble (x amplitude points/s, frequency), size, growth over
# life, life (s), spin (deg/s).
PARTICLES = {
    "bubbles": dict(look="ring", rate=(3, 16, 0, 1), origin="body", vx=(-15, 15), vy=(-90, -40), wind=20,
                    wobble=(14, 4.0), size=(6, 14), grow=0.4, life=(1.6, 2.8), spin=(0, 0)),
    "sparkles": dict(look="star", rate=(4, 26, 0, 1.5), origin="body", vx=(-12, 12), vy=(-12, 12), wind=0,
                     wobble=(0, 0), size=(8, 16), grow=0.0, life=(0.8, 1.6), spin=(-40, 40)),
    "stars": dict(look="star", rate=(3, 18, 0, 1.4), origin="body", vx=(-10, 10), vy=(-30, -10), wind=10,
                  wobble=(6, 2.0), size=(10, 18), grow=0.0, life=(1.4, 2.4), spin=(-90, 90)),
    "notes": dict(look="glyph", rate=(3, 14, 0, 1), origin="body", vx=(-10, 10), vy=(-60, -30), wind=30,
                  wobble=(20, 3.0), size=(18, 28), grow=0.0, life=(1.8, 2.8), spin=(0, 0), glyphs="♪♫"),
    "embers": dict(look="dot", rate=(0, 30, 0.2, 1.3), origin="feet", vx=(-20, 20), vy=(-160, -80), wind=25,
                   wobble=(18, 6.0), size=(2, 4), grow=-0.5, life=(0.7, 1.5), spin=(0, 0)),
    "foxfire": dict(look="wisp", rate=(2, 10, 0, 1), origin="body", vx=(-8, 8), vy=(-45, -20), wind=8,
                    wobble=(16, 2.5), size=(7, 12), grow=-0.3, life=(1.4, 2.4), spin=(0, 0)),
    "confetti": dict(look="chip", rate=(0, 40, 0.4, 1.5), origin="top", vx=(-40, 40), vy=(60, 140), wind=60,
                     wobble=(25, 5.0), size=(5, 9), grow=0.0, life=(1.6, 2.6), spin=(-400, 400)),
    "zzz": dict(look="glyph", rate=(1.2, 0, 0, 1), origin="head", vx=(10, 22), vy=(-30, -18), wind=0,
                wobble=(6, 1.5), size=(14, 18), grow=0.7, life=(2.2, 3.0), spin=(-10, 10), glyphs="Zz", inverse=True),
    "pages": dict(look="chip", rate=(0, 20, 0.2, 1.4), origin="body", vx=(120, 220), vy=(-40, 30), wind=0,
                  wobble=(18, 5.0), size=(10, 16), grow=0.0, life=(1.3, 2.4), spin=(-260, 260)),
    "blossoms": dict(look="chip", rate=(2, 24, 0, 1.5), origin="body", vx=(100, 200), vy=(-30, 30), wind=0,
                     wobble=(18, 5.0), size=(8, 13), grow=0.0, life=(1.4, 2.6), spin=(-200, 200)),
    "feathers": dict(look="feather", rate=(3, 18, 0, 1.4), origin="top", vx=(-20, 20), vy=(25, 50), wind=40,
                     wobble=(30, 1.8), size=(14, 24), grow=0.0, life=(2.0, 3.2), spin=(-60, 60)),
    "snow": dict(look="dot", rate=(6, 20, 0, 1), origin="top", vx=(-10, 10), vy=(30, 60), wind=30,
                 wobble=(12, 2.0), size=(3, 6), grow=0.0, life=(2.5, 4.0), spin=(0, 0)),
}


def particle_palette(kind, accent):
    """Colors (0...1 RGB) of a kind for a character of `accent` (#RRGGBB)."""
    a = tuple(int(accent[i:i + 2], 16) / 255 for i in (1, 3, 5)) if accent else (0.85, 0.47, 0.34)
    light = tuple(0.45 * c + 0.55 for c in a)
    return {"bubbles": [(0.75, 0.9, 1.0)], "sparkles": [light, (1.0, 0.97, 0.85)], "stars": [light, (1.0, 0.9, 0.55)],
            "notes": [light], "embers": [(1.0, 0.55, 0.2), (1.0, 0.8, 0.35)], "foxfire": [(0.45, 0.72, 1.0)],
            "confetti": [a, (1.0, 0.48, 0.72), (1.0, 0.83, 0.35), (0.48, 0.88, 0.76)], "zzz": [light],
            "pages": [(0.98, 0.97, 0.93)], "blossoms": [(1.0, 0.86, 0.92), (0.86, 0.9, 1.0), (1.0, 1.0, 1.0)],
            "feathers": [a, light], "snow": [(1.0, 1.0, 1.0)]}.get(kind, [a])


class Particles:
    """Ambient particles of one kind around the character, emitted with the speed and thrown out in bursts; petals
    are Claude's (the `Petals` drift above)."""

    def __init__(self, kind, area, accent=None, wind_sign=1.0, seed=11):
        self.kind = kind if kind in PARTICLES or kind == "petals" else "petals"
        self.petals = Petals(area, wind_sign, seed) if self.kind == "petals" else None
        self.spec = PARTICLES.get(self.kind)
        self.x0, self.y0, self.x1, self.y1 = area
        self.wind_sign = wind_sign
        self.colors = particle_palette(self.kind, accent)
        self.rng = Rng(seed)
        self.items = []
        self.carry = 0.0

    def _spawn(self, intensity, at=None):
        sp, r = self.spec, self.rng
        w, h = self.x1 - self.x0, self.y1 - self.y0
        if at is not None:
            x, y = at
        elif sp["origin"] == "top":
            x, y = r.uniform(self.x0, self.x1), self.y0 - 0.05 * h + r.uniform(0, 0.25 * h)
        elif sp["origin"] == "head":
            x, y = r.uniform(self.x0 + 0.35 * w, self.x0 + 0.65 * w), r.uniform(self.y0, self.y0 + 0.2 * h)
        elif sp["origin"] == "feet":
            x, y = r.uniform(self.x0 + 0.25 * w, self.x0 + 0.75 * w), self.y1 - r.uniform(0, 0.1 * h)
        else:
            x, y = r.uniform(self.x0 + 0.1 * w, self.x0 + 0.9 * w), r.uniform(self.y0 + 0.1 * h, self.y0 + 0.8 * h)
        p = {"x": x, "y": y, "vx": r.uniform(*sp["vx"]) + self.wind_sign * sp["wind"] * (0.6 + intensity),
             "vy": r.uniform(*sp["vy"]), "size": r.uniform(*sp["size"]), "life": r.uniform(*sp["life"]),
             "spin": r.uniform(*sp["spin"]), "rot": r.uniform(0, 360), "phase": r.uniform(0, 6.28),
             "front": r.random() < 0.55, "color": self.colors[r.index(len(self.colors))], "age": 0.0}
        glyphs = sp.get("glyphs")
        p["glyph"] = glyphs[r.index(len(glyphs))] if glyphs else None
        return p

    def rate(self, intensity):
        base, gain, floor, power = self.spec["rate"]
        if self.spec.get("inverse"):
            return base * (1 - intensity) ** 2
        return base + gain * max(0.0, intensity - floor) ** power

    def update(self, dt, intensity):
        if self.petals is not None:
            self.petals.update(dt, intensity)
            return
        self.carry += self.rate(intensity) * dt
        while self.carry >= 1:
            self.carry -= 1
            self.items.append(self._spawn(intensity))
        amp, freq = self.spec["wobble"]
        alive = []
        for p in self.items:
            p["age"] += dt
            if p["age"] >= p["life"]:
                continue
            p["x"] += (p["vx"] + amp * math.sin(p["age"] * freq + p["phase"])) * dt
            p["y"] += p["vy"] * dt
            p["rot"] += p["spin"] * dt
            alive.append(p)
        self.items = alive

    def burst(self, x, y, count=14, speed=260):
        if self.petals is not None:
            self.petals.burst(x, y, count, speed)
            return
        for _ in range(count):
            p = self._spawn(0.6, at=(x, y))
            a = self.rng.uniform(0, 2 * math.pi)
            v = self.rng.uniform(0.3, 0.8) * speed
            p["vx"], p["vy"] = math.cos(a) * v, math.sin(a) * v * 0.6 - 40
            p["life"] *= 0.7
            self.items.append(p)

    def alpha(self, p):
        k = p["age"] / p["life"]
        a = min(1.0, k / 0.15) * min(1.0, (1 - k) / 0.3)
        if self.spec["look"] == "star":
            a *= 0.55 + 0.45 * math.sin(p["age"] * 9 + p["phase"]) ** 2
        return a

    def size(self, p):
        return p["size"] * (1 + self.spec["grow"] * p["age"] / p["life"])

    def dots(self, front):
        """Rows for Renderer.dots (x, y, rx, ry, r, g, b, a, softness), and whether they add up (glows)."""
        rows, look = [], self.spec["look"] if self.spec else "sprite"
        for p in self.items:
            if p["front"] != front:
                continue
            a, s, (r, g, b) = self.alpha(p), self.size(p), p["color"]
            x, y = p["x"], p["y"]
            if look == "ring":
                rows += [(x, y, s, s, r, g, b, 0.22 * a, 0.25), (x - 0.35 * s, y - 0.35 * s, 0.25 * s, 0.25 * s,
                                                                   1, 1, 1, 0.8 * a, 0.5)]
            elif look == "star":
                rows += [(x, y, s, 0.16 * s, r, g, b, a, 0.6), (x, y, 0.16 * s, s, r, g, b, a, 0.6),
                         (x, y, 0.6 * s, 0.6 * s, r, g, b, 0.35 * a, 1.0)]
            elif look == "wisp":
                rows += [(x, y, s, 1.7 * s, r, g, b, 0.45 * a, 1.0), (x, y + 0.2 * s, 0.4 * s, 0.6 * s, 0.9, 0.97, 1,
                                                                      0.8 * a, 0.6)]
            elif look == "chip":
                flip = abs(math.cos(math.radians(p["rot"])))
                rows.append((x, y, 0.5 * s, 0.5 * s * (0.15 + 0.85 * flip), r, g, b, a, 0.15))
            elif look == "feather":
                rows.append((x, y, 0.3 * s, s, r, g, b, 0.85 * a, 0.5))
            elif look == "dot":
                rows.append((x, y, s, s, r, g, b, a, 0.7))
        return np.array(rows, np.float32).reshape(-1, 9)

    @property
    def additive(self):
        return self.spec is not None and self.spec["look"] in ("star", "wisp") or self.kind == "embers"

    def glyphs(self, front):
        """(glyph, x, y, size, angle, alpha, rgb) of the glyph kinds (notes, zzz)."""
        if not self.spec or self.spec["look"] != "glyph":
            return []
        return [(p["glyph"], p["x"], p["y"], self.size(p), math.radians(p["rot"]), self.alpha(p), p["color"])
                for p in self.items if p["front"] == front]

    def sprites(self, front):
        """Atlas sprite instances: Claude's petals."""
        if self.petals is not None:
            return self.petals.instances(front)
        return np.zeros((0, 6), np.float32)


def glow_strength(intensity, time):
    base = max(0.0, (intensity - 0.55) / 0.45)
    return base * (0.75 + 0.25 * math.sin(time * 6.0)) * 0.7


def shadow_texture(size=128):
    yy, xx = np.mgrid[0:size, 0:size]
    r = np.hypot(xx - size / 2 + 0.5, yy - size / 2 + 0.5) / (size / 2)
    a = np.clip(1 - r, 0, 1) ** 1.6
    rgba = np.zeros((size, size, 4), np.float32)
    rgba[..., 3] = a
    rgba[..., :3] = 0.05
    return (rgba * 255).astype(np.uint8)


def radial_texture(size=256, color=(1.0, 0.56, 0.24)):
    yy, xx = np.mgrid[0:size, 0:size]
    r = np.hypot(xx - size / 2 + 0.5, yy - size / 2 + 0.5) / (size / 2)
    a = np.clip(1 - r, 0, 1) ** 2.2
    rgba = np.zeros((size, size, 4), np.float32)
    rgba[..., :3] = np.array(color) * a[..., None]
    rgba[..., 3] = a
    return (rgba * 255).astype(np.uint8)
