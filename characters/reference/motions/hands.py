"""Hand acting: fingers that breathe, fidget and follow through.

Every hand has a slow ripple running across its fingers, a drifting wrist and a small spread, so it never freezes. Now
and then it plays a fidget: the hand holding the book taps, regrips or lifts its fingers; the free hand flexes, fans,
counts or drums on the beat. When the arm moves the wrist trails behind it and the fingers trail the wrist, and the
springs in springs.py add the overshoot, so motion rolls out from the shoulder to the fingertips.
"""
import math

from .curves import Motion, oscillate
from .profile import POSE_HAND_SIDES, TWO_HANDED

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


def flute(beat):
    """Fingering a transverse flute: index, middle and ring fingers lift and press through a fixed tune, half a beat
    per note. The wrist stays put, so the flute it holds with the head never swings into the mouth."""
    pattern = ((1,), (1, 2), (2, 3), (1, 3), (2,), (1, 2, 3), (3,), (1, 2))
    step = 0.5 * beat
    curves = {f"F{i}": [(0, 0)] for i in (1, 2, 3)}
    for k, lifted in enumerate(pattern):
        t = 0.05 + k * step
        for i in (1, 2, 3):
            v = -0.3 if i in lifted else 0.12
            curves[f"F{i}"] += [(t + 0.06, v), (t + step - 0.04, v)]
    end = 0.05 + len(pattern) * step + 0.1
    for keys in curves.values():
        keys.append((end, 0))
    return Motion("flute", end, curves)


def fanflutter(beat):
    """Three unhurried waves of the wrist holding a fan, then a short rest."""
    d = 1.9
    curves = {"W": [(0, 0)] + oscillate(0.1, d - 0.1, 0.6, 0.22) + [(d, 0)],
              "S": [(0, 0), (0.3, 0.15), (d - 0.3, 0.15), (d, 0)]}
    return Motion("fanflutter", d + 0.5 * beat, curves)


def abacus(beat):
    """Index-finger flicks on an abacus, two per beat (never closer than 0.24 s), the thumb joining every other one."""
    step = max(0.5 * beat, 0.24)
    f1, f0 = [(0, 0)], [(0, 0)]
    for k in range(6):
        t = 0.05 + k * step
        f1 += [(t, 0.0), (t + 0.05, 0.5), (t + 0.11, -0.12), (t + 0.2, 0.0)]
        if k % 2 == 0:
            f0 += [(t, 0.0), (t + 0.06, 0.25), (t + 0.16, 0.0)]
    end = 0.05 + 6 * step + 0.1
    curves = {"F1": f1 + [(end, 0)], "F0": f0 + [(end, 0)],
              "W": [(0, 0)] + oscillate(0.05, end - 0.05, 2 * step, 0.05) + [(end, 0)]}
    return Motion("abacus", end, curves)


def sleeveflop(beat):
    """Slow, loose waves of a hand lost in a long sleeve."""
    d = 2.4
    droop = [(0, 0), (0.4, 0.2), (d - 0.4, 0.2), (d, 0)]
    curves = {"W": [(0, 0)] + oscillate(0.1, d - 0.1, 1.6, 0.3) + [(d, 0)],
              **{f"F{i}": droop for i in (1, 2, 3, 4)}}
    return Motion("sleeveflop", d, curves)


def ripple(beat):
    """Nothing but the resting ripple, for two seconds."""
    return Motion("ripple", 2.0, {"S": [(0, 0), (2.0, 0)]})


def grip(beat):
    """Both hands holding one object up: the fingers tighten a little on every other beat, the wrists still, so the
    hands never leave it and the body sways it from side to side."""
    d = 2 * beat
    squeeze = [(0, 0), (0.35 * d, 0.1), (0.6 * d, 0.1), (d, 0)]
    return Motion("grip", d, {f"F{i}": squeeze for i in (1, 2, 3, 4)})


def make(name, beat):
    return {"tap": lambda: tap(beat), "regrip": regrip, "lift": lift, "flex": flex, "fan": fan,
            "count": lambda: count(beat), "drum": lambda: drum(beat), "flute": lambda: flute(beat),
            "fanflutter": lambda: fanflutter(beat), "abacus": lambda: abacus(beat),
            "sleeveflop": lambda: sleeveflop(beat), "ripple": lambda: ripple(beat), "grip": lambda: grip(beat)}[name]()


class HandActor:
    def __init__(self, side, holds, rng, phase=0.0, sets=()):
        self.side, self.holds, self.rng, self.phase = side, holds, rng, phase
        self.sets = list(sets)
        self.mode = None
        self.mode_action = False
        self.action, self.t = None, 0.0
        self.next = rng.uniform(1.0, 3.0)
        self.prev_arm, self.trail = None, 0.0

    def _mode_on(self):
        if self.mode is None:
            return False
        sides = POSE_HAND_SIDES[self.mode]
        return sides == "both" or (sides == "holder") == self.holds

    def set_mode(self, mode):
        """The pose's hand set: played back to back on the hands it belongs to, from the next action on."""
        self.mode = mode
        if self._mode_on() and self.action is None:
            self.next = min(self.next, 0.0)

    def _choose(self, band, beat):
        if self._mode_on():
            return make(self.mode, beat)
        if self.holds:
            options = [tap(beat), regrip(), lift()]
        elif self.sets:
            options = [make(name, beat) for name in self.sets]
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
        sway = 0.0 if self.mode in TWO_HANDED else (0.03 if self.holds else 0.09)
        out[f"Wrist.{s}"] = sway * math.sin(2 * math.pi * time / 5.3 + self.phase)

        if self.prev_arm is not None:
            vel = (arm - self.prev_arm) / max(dt, 1e-6)
            self.trail += (max(-1.0, min(1.0, -0.35 * vel)) - self.trail) * min(1.0, dt * 10)
        self.prev_arm = arm
        out[f"Wrist.{s}"] += self.trail * (0.25 if self.holds else 0.6)
        for i in range(FINGERS):
            out[f"Finger.{s}.{i}"] -= abs(self.trail) * 0.25

        self.next -= dt * busy
        if self.action is None and self.next <= 0:
            self.mode_action = self._mode_on()
            self.action, self.t = self._choose(band, beat), 0.0
        if self.action is not None:
            self.t += dt
            wrist = 1.0 if self.mode_action or not self.holds else 0.3
            for key, v in self.action.sample(self.t).items():
                if key == "W":
                    out[f"Wrist.{s}"] += v * wrist
                elif key == "S":
                    out[f"Spread.{s}"] += v
                else:
                    out[f"Finger.{s}.{key[1:]}"] += v
            if self.t >= self.action.duration:
                self.action = None
                self.next = 0.0 if self._mode_on() else self.rng.uniform(1.6, 4.2)
        return out
