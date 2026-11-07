"""Speed effects: drifting petals carried by the wind and a warm glow at the top speeds."""
import math
import random

import numpy as np


class Petals:
    def __init__(self, area, wind_sign=1.0, seed=11):
        self.x0, self.y0, self.x1, self.y1 = area
        self.wind_sign = wind_sign
        self.rng = random.Random(seed)
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


def glow_strength(intensity, time):
    base = max(0.0, (intensity - 0.55) / 0.45)
    return base * (0.75 + 0.25 * math.sin(time * 6.0)) * 0.7


def radial_texture(size=256, color=(1.0, 0.56, 0.24)):
    yy, xx = np.mgrid[0:size, 0:size]
    r = np.hypot(xx - size / 2 + 0.5, yy - size / 2 + 0.5) / (size / 2)
    a = np.clip(1 - r, 0, 1) ** 2.2
    rgba = np.zeros((size, size, 4), np.float32)
    rgba[..., :3] = np.array(color) * a[..., None]
    rgba[..., 3] = a
    return (rgba * 255).astype(np.uint8)
