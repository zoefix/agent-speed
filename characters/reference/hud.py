"""The floating readout of the character style: speed, title, model, agents and events drift around the character.

The speed, the model and the agent line orbit her like small moons (orbits.py): they pass in front of her and behind
her, bigger and brighter in front, and the orbits' center trails her on a spring when she hops or steps. A ring of lights at her feet is the gauge: it fills with the heat colors as the
speed rises and a bright comet runs around it; its front half is drawn over her, its back half behind, so it reads as
lying on the floor. Agents are small lights orbiting her waist. Numbers roll digit by digit.
"""
import math
from functools import lru_cache

import numpy as np
from PIL import Image, ImageDraw, ImageFont

from .motions.springs import SecondOrder
from .orbits import Orbit
from .rng import Rng

DIGITS_FONT = "/System/Library/Fonts/SFNS.ttf"
CJK_FONT = "/System/Library/Fonts/STHeiti Medium.ttc"
HEAT = [(0.0, (61, 220, 132)), (0.35, (255, 213, 74)), (0.7, (255, 138, 61)), (1.0, (255, 59, 59))]
WHITE_HOT = (255, 247, 214)
GOLD = (255, 200, 80)


def heat_color(h):
    h = min(max(h, 0.0), 1.0)
    for (h0, c0), (h1, c1) in zip(HEAT, HEAT[1:]):
        if h <= h1:
            k = (h - h0) / (h1 - h0)
            return tuple(int(a + (b - a) * k) for a, b in zip(c0, c1))
    return HEAT[-1][1]


@lru_cache(maxsize=32)
def _font(path, size, weight=None):
    f = ImageFont.truetype(path, size)
    if weight:
        try:
            f.set_variation_by_name(weight)
        except (OSError, ValueError):
            pass
    return f


def _text(text, font, color, pad=4):
    box = font.getbbox(text)
    w, h = box[2] - box[0] + 2 * pad, box[3] - box[1] + 2 * pad
    img = Image.new("RGBA", (max(w, 1), max(h, 1)), (0, 0, 0, 0))
    ImageDraw.Draw(img).text((pad - box[0], pad - box[1]), text, font=font, fill=color)
    return np.asarray(img)


def _pill(content, fill, rim, pad_x, pad_y):
    h, w = content.shape[:2]
    W, H = w + 2 * pad_x, h + 2 * pad_y
    img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((0, 0, W - 1, H - 1), radius=H // 2, fill=fill)
    d.rounded_rectangle((0, 0, W - 1, H - 1), radius=H // 2, outline=rim, width=1)
    img.alpha_composite(Image.fromarray(content), (pad_x, pad_y))
    return np.asarray(img)


class Textures:
    """Text and chip textures, made once per distinct content."""

    def __init__(self, renderer, unit):
        self.r, self.u, self.cache = renderer, unit, {}

    def get(self, key, make):
        if key not in self.cache:
            rgba = make()
            self.cache[key] = (self.r.texture(rgba, mipmaps=True), rgba.shape[1], rgba.shape[0])
        return self.cache[key]

    def digit(self, ch, size):
        return self.get(("digit", ch, size), lambda: _text(ch, _font(DIGITS_FONT, size, b"Bold"), (250, 251, 253, 246)))

    def label(self, text, size, color, weight=b"Medium"):
        font = _font(CJK_FONT if any(ord(c) > 0x2000 for c in text) else DIGITS_FONT, size, weight)
        return self.get(("label", text, size, color), lambda: _text(text, font, color))

    def chip(self, text, size, color, fill, rim, dot=None):
        def make():
            font = _font(CJK_FONT if any(ord(c) > 0x2E80 for c in text) else DIGITS_FONT, size, b"Semibold")
            body = _text(text, font, color)
            if dot is not None:
                d = int(size * 0.55)
                lead = np.zeros((body.shape[0], d + int(size * 0.45), 4), np.uint8)
                img = Image.fromarray(lead)
                ImageDraw.Draw(img).ellipse((0, (body.shape[0] - d) // 2, d, (body.shape[0] + d) // 2), fill=dot + (255,))
                body = np.concatenate([np.asarray(img), body], axis=1)
            return _pill(body, fill, rim, int(size * 0.7), int(size * 0.32))
        return self.get(("chip", text, size, color, fill, rim, dot), make)


class Follow:
    """A 2D point that trails its target on springs and bobs on its own phase."""

    def __init__(self, f=1.6, z=0.7, bob=(5.0, 3.6), phase=0.0):
        self.x, self.y = SecondOrder(f, z, 0.0), SecondOrder(f, z, 0.0)
        self.bob, self.phase, self.placed = bob, phase, False

    def snap(self, x, y):
        self.x.snap(x)
        self.y.snap(y)
        self.placed = True

    def update(self, dt, t, tx, ty):
        if not self.placed:
            self.snap(tx, ty)
        amp, period = self.bob
        return (self.x.update(dt, tx) + 0.35 * amp * math.sin(2 * math.pi * t / (period * 1.7) + self.phase),
                self.y.update(dt, ty) + amp * math.sin(2 * math.pi * t / period + self.phase))


class Odometer:
    """Integer readout whose digits roll: a changed digit slides up out of view as the new one slides in. The value
    is sampled five times a second, like an instrument, so a fast-changing speed still reads cleanly."""

    HOLD = 0.2

    def __init__(self):
        self.value, self.digits, self.held, self.since = 0.0, [], 0, 1.0

    def update(self, dt, target):
        self.value += (target - self.value) * min(1.0, dt * 7.0)
        self.since += dt
        if self.since >= self.HOLD:
            self.held, self.since = int(round(self.value)), 0.0
        text = str(self.held)
        while len(self.digits) < len(text):
            self.digits.insert(0, {"cur": None, "prev": None, "t": 1.0})
        while len(self.digits) > len(text):
            self.digits.pop(0)
        for d, ch in zip(self.digits, text):
            if d["cur"] != ch:
                d["prev"], d["cur"], d["t"] = d["cur"], ch, 0.0
            d["t"] = min(1.0, d["t"] + dt / 0.12)
        return text


class Hud:
    def __init__(self, renderer, unit, seed=5):
        self.r, self.u = renderer, unit
        self.tex = Textures(renderer, unit)
        self.rng = Rng(seed)
        self.center = Follow(1.1, 0.75, (0.0, 1.0), 0.0)
        self.peak_pos = Follow(2.0, 0.6, (3.0 * unit, 2.9), 0.7)
        self.orbits, self.placed = None, {}
        self.odo = Odometer()
        self.width = SecondOrder(2.5, 0.8, 0.0)
        self.title, self.title_prev, self.title_t = None, None, 1.0
        self.comet = 0.0
        self.pulses, self.embers = [], []
        self.peak_t = None
        self.orbit = 0.0

    def pulse(self, strength=1.0):
        self.pulses.append({"t": 0.0, "k": strength})

    def peak(self):
        self.peak_t = 0.0
        self.peak_pos.placed = False

    def update(self, dt, t, speed, title, h, fast):
        self.comet = (self.comet + dt / (6.0 - 5.3 * h)) % 1.0
        self.orbit += dt * (0.25 + 0.9 * h)
        if title != self.title:
            self.title_prev, self.title, self.title_t = self.title, title, 0.0
        self.title_t = min(1.0, self.title_t + dt / 0.35)
        for p in self.pulses:
            p["t"] += dt
        self.pulses = [p for p in self.pulses if p["t"] < 0.9]
        if self.peak_t is not None:
            self.peak_t += dt
            if self.peak_t > 2.4:
                self.peak_t = None
        self.speed, self.h, self.fast, self.t = speed, h, fast, t

    def _ring(self, cx, cy, rx, ry, front):
        n = 96
        lit = self.h * n
        rows = []
        for i in range(n):
            a = math.pi / 2 + 2 * math.pi * i / n
            s = math.sin(a)
            if (s >= 0) != front:
                continue
            x, y = cx + rx * math.cos(a), cy + ry * s
            depth = 0.5 + 0.5 * (s + 1) / 2
            size = (2.2 + 1.6 * (s + 1) / 2) * self.u
            comet = math.exp(-(((i / n - self.comet + 0.5) % 1.0 - 0.5) * 18) ** 2)
            if i < lit:
                c = np.array(WHITE_HOT if i == int(lit) else heat_color(i / n)) / 255
                k = min(1.0, lit - i) if i != int(lit) else 1.0
                rows.append((x, y, size * 3.2, size * 1.7, *c, (0.16 + 0.3 * comet) * depth * k, 1.0))
                rows.append((x, y, size, size * 0.62, *c, (0.75 + 0.25 * comet) * depth * k, 0.35))
            else:
                rows.append((x, y, size * 0.75, size * 0.47, 1, 1, 1, (0.16 + 0.3 * comet * (0.4 + self.h)) * depth,
                             0.4))
        return np.array(rows, np.float32).reshape(-1, 9)

    def _pulses(self, cx, cy, rx, ry, front):
        rows = []
        for p in self.pulses:
            k = p["t"] / 0.9
            ease = 1 - (1 - k) ** 3
            c = np.array(heat_color(self.h)) / 255
            for i in range(96):
                a = 2 * math.pi * i / 96
                s = math.sin(a)
                if (s >= 0) != front:
                    continue
                grow = 1 + 0.42 * ease
                rows.append((cx + rx * grow * math.cos(a), cy + ry * grow * s, 1.6 * self.u, 1.0 * self.u, *c,
                             0.55 * (1 - k) * p["k"], 0.5))
        return np.array(rows, np.float32).reshape(-1, 9)

    def _satellites(self, cx, cy, rx, ry, agents, front):
        rows = []
        for k, color in enumerate(agents):
            base = self.orbit + 2 * math.pi * k / max(1, len(agents))
            for j in range(6):
                a = base - j * 0.07
                s = math.sin(a)
                if (s >= 0) != front:
                    continue
                tilt = -0.14
                x = cx + rx * math.cos(a)
                y = cy + ry * s + tilt * rx * math.cos(a)
                depth = 0.6 + 0.4 * (s + 1) / 2
                c = np.array(color) / 255
                if j == 0:
                    rows.append((x, y, 18 * self.u * depth, 18 * self.u * depth, *c, 0.45 * depth, 1.0))
                    rows.append((x, y, 4.5 * self.u * depth, 4.5 * self.u * depth, *(c * 0.35 + 0.65), depth, 0.3))
                else:
                    r = (3.6 - 0.45 * j) * self.u * depth
                    rows.append((x, y, r, r, *c, (0.55 - 0.08 * j) * depth, 0.6))
        return np.array(rows, np.float32).reshape(-1, 9)

    def _embers(self, dt, cx, cy, rx, ry):
        rate = 70 * max(0.0, (self.h - 0.6) / 0.4) * self.fast
        n = int(rate * dt + self.rng.random())
        for _ in range(n):
            a = self.rng.uniform(0, 2 * math.pi)
            self.embers.append({"x": cx + rx * math.cos(a), "y": cy + ry * math.sin(a), "vy": -self.rng.uniform(60, 160),
                                "age": 0.0, "life": self.rng.uniform(0.7, 1.5), "seed": self.rng.uniform(0, 6.28),
                                "front": math.sin(a) > 0})
        alive = []
        for e in self.embers:
            e["age"] += dt
            if e["age"] < e["life"]:
                e["y"] += e["vy"] * self.u * dt
                e["x"] += 18 * self.u * math.sin(e["age"] * 6 + e["seed"]) * dt
                alive.append(e)
        self.embers = alive

    def _ember_rows(self, front):
        rows = []
        for e in self.embers:
            if e["front"] != front:
                continue
            k = e["age"] / e["life"]
            c = np.array(heat_color(0.7 + 0.3 * (1 - k))) / 255
            rows.append((e["x"], e["y"], 1.8 * self.u, 1.8 * self.u, *c, 0.8 * (1 - k), 0.6))
        return np.array(rows, np.float32).reshape(-1, 9)

    def draw_back(self, dt, ground, waist, agents):
        cx, cy, rx, ry = ground
        self._embers(dt, cx, cy, rx, ry)
        c = np.array(heat_color(self.h)) / 255
        self.r.dots(np.array([(cx, cy - ry * 0.1, rx * 0.55, ry * 0.9, 0.02, 0.02, 0.04, 0.55, 1.0)], np.float32))
        self.r.dots(np.array([(cx, cy, rx * 1.2, ry * 1.8, *c, 0.12 + 0.3 * self.h, 1.0)], np.float32), additive=True)
        self.r.dots(self._ring(cx, cy, rx, ry, front=False), additive=True)
        self.r.dots(self._pulses(cx, cy, rx, ry, front=False), additive=True)
        self.r.dots(self._satellites(*waist, agents, front=False), additive=True)
        self.r.dots(self._ember_rows(front=False), additive=True)

    def draw_front(self, dt, ground, waist, agents):
        cx, cy, rx, ry = ground
        self.r.dots(self._ring(cx, cy, rx, ry, front=True), additive=True)
        self.r.dots(self._pulses(cx, cy, rx, ry, front=True), additive=True)
        self.r.dots(self._satellites(*waist, agents, front=True), additive=True)
        self.r.dots(self._ember_rows(front=True), additive=True)

    def orbit_bodies(self, dt, center, char_h):
        """Places the floating readouts on their orbits around the character's center."""
        if self.orbits is None:
            k = char_h
            self.orbits = {
                "speed": Orbit(0.4 * k, 0.2, 52.0, 0.35, 0.0, -0.06 * k, seed=0.4),
                "model": Orbit(0.34 * k, 0.26, 38.0, 2.4, 0.9, -0.34 * k, seed=1.9),
                "info": Orbit(0.38 * k, 0.22, 45.0, 4.3, -0.6, 0.22 * k, seed=3.1),
            }
        cx, cy = self.center.update(dt, self.t, *center)
        self.placed = {}
        for name, orbit in self.orbits.items():
            x, y, depth = orbit.at(self.t, cx, cy)
            scale = 0.82 + 0.2 * (depth + 1) / 2
            alpha = 0.55 + 0.45 * (depth + 1) / 2
            self.placed[name] = (x, y, depth, scale, orbit.spin(self.t), alpha)

    def _put(self, tex, tw, th, x, y, ox, oy, scale, angle, alpha):
        c, s = math.cos(angle), math.sin(angle)
        px, py = x + (ox * c - oy * s) * scale, y + (ox * s + oy * c) * scale
        self.r.sprites(tex, np.array([[px, py, tw * scale, th * scale, angle, alpha]], np.float32))

    def draw_bodies(self, dt, front, model_name, info_line, dot):
        bodies = sorted((v[2], k) for k, v in self.placed.items() if (v[2] >= 0) == front)
        for _, name in bodies:
            if name == "speed":
                self._draw_speed(dt, *self.placed[name])
            elif name == "model":
                x, y, _, scale, angle, alpha = self.placed[name]
                tex, tw, th = self.tex.chip(model_name, int(22 * self.u), (238, 242, 248, 240), (16, 18, 26, 150),
                                            (255, 255, 255, 40), dot=dot)
                self._put(tex, tw, th, x, y, 0, 0, scale, angle, alpha)
            else:
                x, y, _, scale, angle, alpha = self.placed[name]
                tex, tw, th = self.tex.chip(info_line, int(17 * self.u), (214, 220, 232, 230), (16, 18, 26, 120),
                                            (255, 255, 255, 28))
                self._put(tex, tw, th, x, y, 0, 0, scale, angle, alpha)

    def _draw_speed(self, dt, x, y, depth, scale, angle, alpha, unit_text="tok/s"):
        u, h = self.u, self.h
        text = self.odo.update(dt, self.speed)
        size = int(92 * u)
        adv = self.tex.digit("0", size)[1] * 0.9
        width = self.width.update(dt, adv * len(text))
        c = np.array(heat_color(h)) / 255
        glow = (0.12 + 0.5 * max(0.0, h - 0.3)) * alpha
        self.r.dots(np.array([(x, y + 40 * u * scale, (width * 0.8 + 70 * u) * scale, 120 * u * scale,
                               0.02, 0.025, 0.045, 0.62 * alpha, 1.0)], np.float32))
        self.r.dots(np.array([(x, y, (width * 0.75 + 30 * u) * scale, 50 * u * scale, *c, glow, 1.0)], np.float32),
                    additive=True)
        line = self.tex.digit("0", size)[2]
        hw, hh = (width / 2 + 8 * u) * scale, 0.5 * line * scale
        ca, sa = abs(math.cos(angle)), abs(math.sin(angle))
        self.r.clip((x - hw * ca - hh * sa, y - hw * sa - hh * ca, x + hw * ca + hh * sa, y + hw * sa + hh * ca))
        for i, d in enumerate(self.odo.digits):
            ox = -width / 2 + adv * (i + 0.5)
            ease = 1 - (1 - d["t"]) ** 3
            for ch, dy, a in ((d["prev"], -ease, 1 - 0.6 * ease), (d["cur"], 1 - ease, 0.4 + 0.6 * ease)):
                if ch is None:
                    continue
                tex, tw, th = self.tex.digit(ch, size)
                self._put(tex, tw, th, x, y, ox, dy * 0.8 * th, scale, angle, a * alpha)
        self.r.clip(None)
        tex, tw, th = self.tex.label(unit_text, int(21 * u), (205, 212, 224, 170))
        self._put(tex, tw, th, x, y, 0, 50 * u + th / 2, scale, angle, alpha)
        for title, k in ((self.title_prev, -1), (self.title, 1)):
            if title is None:
                continue
            e = 1 - (1 - self.title_t) ** 3
            a = max(0.0, (e - 0.3) / 0.7) if k > 0 else max(0.0, 1 - 2.5 * e)
            if a <= 0.01:
                continue
            col = heat_color(h)
            tex, tw, th = self.tex.chip(title, int(19 * u), col + (255,), (16, 18, 26, 150), col + (90,))
            off = (1 - e) * 14 * u if k > 0 else -e * 14 * u
            self._put(tex, tw, th, x, y, 0, 86 * u + th / 2 + off, scale, angle, a * alpha)

    def draw_peak(self, dt, anchor):
        if self.peak_t is None:
            return
        x, y = self.peak_pos.update(dt, self.t, *anchor)
        t = self.peak_t
        pop = 1 + 0.25 * math.exp(-6 * t) * math.sin(14 * t) if t > 0 else 1
        a = min(1.0, t / 0.15) * min(1.0, (2.4 - t) / 0.4)
        tex, tw, th = self.tex.chip("新紀錄  NEW PEAK", int(17 * self.u), GOLD + (255,), (40, 30, 10, 170), GOLD + (120,))
        w, h = tw * pop * min(1.0, 0.6 + t * 3), th * pop * min(1.0, 0.6 + t * 3)
        rays = []
        for i in range(24):
            ang = 2 * math.pi * i / 24
            dist = (40 + 160 * min(1.0, t / 0.6)) * self.u
            rays.append((x + dist * math.cos(ang), y + dist * 0.5 * math.sin(ang), 2.2 * self.u, 2.2 * self.u,
                         *(np.array(GOLD) / 255), max(0.0, 0.8 - t), 0.6))
        self.r.dots(np.array(rays, np.float32), additive=True)
        self.r.image(tex, x - w / 2, y - h / 2, w, h, alpha=a)
