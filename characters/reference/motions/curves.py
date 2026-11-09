"""Keyframed motion curves.

Values pass through the keys on a monotone cubic (PCHIP, Fritsch-Carlson): holds stay flat, peaks stop smoothly and a
run in one direction never pauses at a key. Ends have zero slope, so a motion starts and stops at rest; looping
curves join smoothly across the wrap.
"""
import math
from bisect import bisect_right


def _slopes(t, v, periodic):
    n = len(t)
    m = [0.0] * n
    if n < 3:
        return m
    h = [t[i + 1] - t[i] for i in range(n - 1)]
    d = [(v[i + 1] - v[i]) / h[i] if h[i] > 1e-9 else 0.0 for i in range(n - 1)]

    def blend(h0, h1, d0, d1):
        if d0 * d1 <= 0:
            return 0.0
        w1, w2 = 2 * h1 + h0, h1 + 2 * h0
        return (w1 + w2) / (w1 / d0 + w2 / d1)

    for i in range(1, n - 1):
        m[i] = blend(h[i - 1], h[i], d[i - 1], d[i])
    if periodic and abs(v[0] - v[-1]) < 1e-9:
        m[0] = m[-1] = blend(h[-1], h[0], d[-1], d[0])
    return m


class Curve:
    def __init__(self, keys, periodic=False):
        keys = sorted((float(t), float(v)) for t, v in keys)
        self.t = [k[0] for k in keys]
        self.v = [k[1] for k in keys]
        self.m = _slopes(self.t, self.v, periodic)

    @property
    def start(self):
        return self.v[0]

    def __call__(self, x):
        t, v, m = self.t, self.v, self.m
        if x <= t[0]:
            return v[0]
        if x >= t[-1]:
            return v[-1]
        i = bisect_right(t, x) - 1
        h = t[i + 1] - t[i]
        if h <= 1e-9:
            return v[i + 1]
        s = (x - t[i]) / h
        return ((1 + 2 * s) * (1 - s) ** 2 * v[i] + s * (1 - s) ** 2 * h * m[i]
                + s * s * (3 - 2 * s) * v[i + 1] + s * s * (s - 1) * h * m[i + 1])


class Motion:
    """Curves over time. One-shot motions may carry an expression, lip-flap (`talk`), timed events and a sideways
    `travel` that stays after they end."""

    def __init__(self, name, duration, curves, loop=False, fade=0.12, expression=None, talk=False, events=(),
                 travel=0.0):
        self.name, self.duration, self.loop, self.fade = name, duration, loop, fade
        self.expression, self.talk, self.travel = expression, talk, travel
        self.events = sorted(events)
        self.keys = {p: sorted(k) for p, k in curves.items()}
        self.curves = {p: Curve(k, periodic=loop) for p, k in self.keys.items()}

    def sample(self, t):
        t = t % self.duration if self.loop else min(t, self.duration)
        return {p: c(t) for p, c in self.curves.items()}

    def starts(self):
        return {p: c.start for p, c in self.curves.items()}


def shift(keys, dt):
    """Keys delayed by dt, holding the first value meanwhile."""
    if dt <= 0:
        return list(keys)
    return [(0.0, keys[0][1])] + [(t + dt, v) for t, v in keys]


def merge(name, motions, delays=None, **kw):
    """Plays motions together; a later motion wins a parameter both use."""
    delays = delays or [0.0] * len(motions)
    curves, events, end = {}, [], 0.0
    for m, dt in zip(motions, delays):
        for p, k in m.keys.items():
            curves[p] = shift(k, dt)
        events += [(t + dt, e) for t, e in m.events]
        end = max(end, m.duration + dt)
    first = next((m for m in reversed(motions) if m.expression), motions[-1])
    kw.setdefault("expression", first.expression)
    kw.setdefault("talk", any(m.talk for m in motions))
    kw.setdefault("travel", sum(m.travel for m in motions))
    return Motion(name, end, curves, events=events, **kw)


def loop_keys(period, values):
    n = len(values)
    return [(period * i / n, v) for i, v in enumerate(values)] + [(period, values[0])]


def oscillate(start, end, period, amplitude, base=0.0, phase=0.0):
    """Quarter-period keys of a sine from start to end, settling on `base`."""
    keys, t, k = [], start, 0
    step = period / 4
    while t < end - 1e-6:
        keys.append((t, base + amplitude * math.sin(phase + k * math.pi / 2)))
        t += step
        k += 1
    keys.append((end, base))
    return keys


def ballistic(start, air, height, samples=10):
    """A jump arc: up from the ground at `start`, `height` at the top, back down after `air` seconds."""
    return [(start + air * k / samples, height * 4 * (k / samples) * (1 - k / samples)) for k in range(samples + 1)]
