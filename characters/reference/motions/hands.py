"""Hand acting: fingers that breathe, fidget and follow through.

Every hand has a slow ripple running across its fingers, a drifting wrist and a small spread, so it never freezes. Now
and then it plays a fidget: the hand holding the book taps, regrips or lifts its fingers; the free hand flexes, fans,
counts or drums on the beat. When the arm moves the wrist trails behind it and the fingers trail the wrist, and the
springs in springs.py add the overshoot, so motion rolls out from the shoulder to the fingertips.
"""
import math

from .curves import Motion

FINGERS = 5


def _stagger(name, d, per_finger, order, gap):
    """One finger curve played on several fingers, each `gap` seconds after the previous one in `order`."""
    curves = {}
    for k, i in enumerate(order):
        dt = k * gap
        curves[f"F{i}"] = [(0, 0)] + [(t + dt, v) for t, v in per_finger] + [(d + dt + 0.2, 0)]
    return Motion(name, d + gap * (len(order) - 1) + 0.25, curves)


def tap(beat):
    one = [(0.08, -0.25), (0.18, 0.3), (0.3, 0.0)]
    keys = one + [(t + beat, v) for t, v in one]
    return _stagger("tap", 0.3 + beat, keys, (1, 2), 0.12)


def regrip():
    m = _stagger("regrip", 0.7, [(0.2, 0.32), (0.45, 0.28), (0.7, 0.0)], (4, 3, 2, 1, 0), 0.05)
    m.keys["W"] = [(0, 0), (0.25, -0.12), (0.8, 0)]
    return Motion("regrip", m.duration, m.keys)


def lift():
    return _stagger("lift", 0.6, [(0.15, -0.22), (0.35, -0.22), (0.6, 0.0)], (1, 2, 3, 4), 0.07)


def flex():
    m = _stagger("flex", 1.2, [(0.25, 0.4), (0.55, 0.35), (0.85, -0.15), (1.2, 0.0)], (4, 3, 2, 1, 0), 0.06)
    m.keys["S"] = [(0, 0), (0.6, 0), (0.95, 0.45), (1.5, 0)]
    m.keys["W"] = [(0, 0), (0.3, 0.2), (0.9, -0.15), (1.5, 0)]
    return Motion("flex", m.duration, m.keys)


def fan():
    m = _stagger("fan", 1.0, [(0.3, -0.18), (0.7, -0.15), (1.0, 0.0)], (0, 1, 2, 3, 4), 0.04)
    m.keys["S"] = [(0, 0), (0.35, 0.6), (0.75, 0.5), (1.2, 0)]
    m.keys["W"] = [(0, 0), (0.35, -0.2), (1.2, 0)]
    return Motion("fan", m.duration, m.keys)


def count(beat):
    curves = {}
    for i in range(FINGERS):
        t = 0.1 + i * beat
        curves[f"F{i}"] = [(0, 0), (t, 0), (t + 0.12, 0.42), (5 * beat + 0.2, 0.42), (5 * beat + 0.55, 0.0)]
    curves["W"] = [(0, 0)] + [(0.1 + i * beat + 0.12, 0.06 * (-1) ** i) for i in range(FINGERS)] + [(5 * beat + 0.6, 0)]
    return Motion("count", 5 * beat + 0.6, curves)


def drum(beat, bars=2):
    curves = {"F1": [(0, 0)], "F2": [(0, 0)], "F3": [(0, 0)]}
    t = 0.05
    for k in range(4 * bars):
        for i, dt in ((3, 0.0), (2, 0.06), (1, 0.12)):
            curves[f"F{i}"] += [(t + dt, -0.15), (t + dt + 0.07, 0.3), (t + dt + 0.16, 0.0)]
        t += beat
    for k in curves.values():
        k.append((t + 0.2, 0))
    return Motion("drum", t + 0.2, curves)


class HandActor:
    def __init__(self, side, holds, rng, phase=0.0):
        self.side, self.holds, self.rng, self.phase = side, holds, rng, phase
        self.action, self.t = None, 0.0
        self.next = rng.uniform(1.0, 3.0)
        self.prev_arm, self.trail = None, 0.0

    def _choose(self, band, beat):
        if self.holds:
            options = [tap(beat), regrip(), lift()]
        else:
            options = [flex(), fan()] + ([count(beat), drum(beat)] if band in ("boost", "overdrive", "redline") else [])
        return self.rng.choice(options)

    def update(self, dt, time, band, beat, arm):
        s = self.side
        busy = {"idle": 0.5, "cruise": 0.8, "boost": 1.1, "overdrive": 1.4, "redline": 1.7}[band]
        rest = 0.06 if self.holds else 0.14
        out = {f"Finger.{s}.{i}": rest + (0.04 if self.holds else 0.07) * math.sin(
            2 * math.pi * time / 3.4 - 0.55 * i + self.phase) for i in range(FINGERS)}
        out[f"Spread.{s}"] = 0.08 * math.sin(2 * math.pi * time / 4.6 + 1.1 + self.phase)
        out[f"Wrist.{s}"] = (0.03 if self.holds else 0.09) * math.sin(2 * math.pi * time / 5.3 + self.phase)

        if self.prev_arm is not None:
            vel = (arm - self.prev_arm) / max(dt, 1e-6)
            self.trail += (max(-1.0, min(1.0, -0.35 * vel)) - self.trail) * min(1.0, dt * 10)
        self.prev_arm = arm
        out[f"Wrist.{s}"] += self.trail * (0.25 if self.holds else 0.6)
        for i in range(FINGERS):
            out[f"Finger.{s}.{i}"] -= abs(self.trail) * 0.25

        self.next -= dt * busy
        if self.action is None and self.next <= 0:
            self.action, self.t = self._choose(band, beat), 0.0
        if self.action is not None:
            self.t += dt
            for key, v in self.action.sample(self.t).items():
                if key == "W":
                    out[f"Wrist.{s}"] += v * (0.3 if self.holds else 1.0)
                elif key == "S":
                    out[f"Spread.{s}"] += v
                else:
                    out[f"Finger.{s}.{key[1:]}"] += v
            if self.t >= self.action.duration:
                self.action = None
                self.next = self.rng.uniform(1.6, 4.2)
        return out
