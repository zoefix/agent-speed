"""Kepler orbits for the floating readouts: each piece of text circles the character like a small moon.

An orbit is an ellipse in a horizontal plane through the character, seen slightly from above, so the front half sits
lower on screen and closer (bigger, brighter) and the back half higher and behind her. Kepler's equation sets the
pace: a body moves faster near the character and slower far from her. The ellipse slowly breathes, precesses and bobs,
so no two laps look alike.
"""
import math


def eccentric_anomaly(mean, e):
    E = mean if e < 0.8 else math.pi
    for _ in range(6):
        E -= (E - e * math.sin(E) - mean) / (1 - e * math.cos(E))
    return E


class Orbit:
    def __init__(self, a, e, period, phase, omega, height, tilt=0.16, precess=0.012, seed=0.0):
        self.a, self.e, self.period, self.phase = a, e, period, phase
        self.omega, self.height, self.tilt, self.precess, self.seed = omega, height, tilt, precess, seed

    def at(self, t, cx, cy):
        """(x, y, depth) on screen; depth runs from -1 (farthest behind) to 1 (nearest in front)."""
        s = self.seed
        a = self.a * (1 + 0.07 * math.sin(0.11 * t + s) + 0.04 * math.sin(0.27 * t + 2.3 * s))
        mean = 2 * math.pi * t / self.period + self.phase
        E = eccentric_anomaly(mean % (2 * math.pi), self.e)
        px = a * (math.cos(E) - self.e)
        pz = a * math.sqrt(1 - self.e * self.e) * math.sin(E)
        w = self.omega + self.precess * t
        x = px * math.cos(w) - pz * math.sin(w)
        z = px * math.sin(w) + pz * math.cos(w)
        bob = 0.035 * self.a * math.sin(0.37 * t + 1.7 * s) + 0.02 * self.a * math.sin(0.91 * t + s)
        y = cy + self.height + z * self.tilt + bob
        return cx + x, y, max(-1.0, min(1.0, z / (self.a * (1 + self.e))))

    def spin(self, t):
        """A slow tumble of the text, in radians."""
        return 0.055 * math.sin(2 * math.pi * t / 13.0 + self.seed) + 0.03 * math.sin(2 * math.pi * t / 5.3 + 2 * self.seed)
