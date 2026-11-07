"""Motions and the director that turns speed into motion.

A motion sets target values for some parameters over time; the director crossfades between them and adds the
procedural layers (breathing, blinking, glances, sway) whose strength and tempo follow the speed.
"""
import math
import random

LEVELS_PER_BAND = [(0, "idle"), (1, "cruise"), (5, "boost"), (9, "overdrive"), (12, "redline")]


def ease(t):
    return t * t * (3 - 2 * t)


class Motion:
    def __init__(self, name, duration, curves, loop=True, fade=0.4):
        self.name, self.duration, self.loop, self.fade = name, duration, loop, fade
        self.curves = {p: sorted(keys) for p, keys in curves.items()}

    def sample(self, t):
        if self.loop:
            t %= self.duration
        else:
            t = min(t, self.duration)
        out = {}
        for p, keys in self.curves.items():
            if t <= keys[0][0]:
                out[p] = keys[0][1]
                continue
            for (t0, v0), (t1, v1) in zip(keys, keys[1:]):
                if t <= t1:
                    out[p] = v0 + (v1 - v0) * ease((t - t0) / max(t1 - t0, 1e-6))
                    break
            else:
                out[p] = keys[-1][1]
        return out


def loop_keys(period, values):
    n = len(values)
    return [(period * i / n, v) for i, v in enumerate(values)] + [(period, values[0])]


def library(arm_out="right", arm_book="left"):
    a, b = f"Arm.{arm_out}", f"Arm.{arm_book}"
    fa, fb = f"Forearm.{arm_out}", f"Forearm.{arm_book}"
    return {
        "read": Motion("read", 6.0, {
            "AngleY": loop_keys(6.0, [-0.55, -0.6, -0.5, -0.6]),
            "AngleZ": loop_keys(6.0, [-0.35, -0.4, -0.3, -0.38]),
            "AngleX": loop_keys(6.0, [-0.25, -0.2, -0.3, -0.22]),
            "EyeBallX": loop_keys(6.0, [-0.5, -0.2, -0.55, -0.15]),
            "EyeBallY": [(0, -0.7), (6.0, -0.7)],
            b: [(0, 0.15), (6.0, 0.15)],
            fb: [(0, -0.1), (6.0, -0.1)],
        }),
        "calm": Motion("calm", 5.0, {
            "AngleX": loop_keys(5.0, [-0.35, -0.25, -0.4, -0.3]),
            "AngleY": loop_keys(5.0, [0.0, 0.05, -0.05, 0.02]),
            "AngleZ": loop_keys(5.0, [0.1, 0.2, 0.05, 0.15]),
            "EyeBallX": [(0, -0.45), (5.0, -0.45)],
        }),
        "brisk": Motion("brisk", 2.4, {
            "AngleZ": loop_keys(2.4, [-0.25, 0.25]),
            "BodyAngleZ": loop_keys(2.4, [0.2, -0.2]),
            a: loop_keys(2.4, [0.28, -0.18]),
            fa: loop_keys(2.4, [0.05, -0.25]),
            "AngleY": loop_keys(2.4, [0.1, 0.0]),
            "AngleX": [(0, -0.3), (2.4, -0.3)],
        }, fade=0.35),
        "dash": Motion("dash", 1.4, {
            "AngleZ": loop_keys(1.4, [-0.4, 0.35]),
            "BodyAngleZ": loop_keys(1.4, [0.45, -0.1]),
            a: loop_keys(1.4, [0.5, -0.25]),
            fa: loop_keys(1.4, [0.1, -0.45]),
            b: loop_keys(1.4, [0.25, 0.05]),
            "AngleY": [(0, 0.2), (1.4, 0.2)],
            "AngleX": [(0, -0.45), (1.4, -0.45)],
        }, fade=0.3),
        "max": Motion("max", 1.0, {
            a: loop_keys(1.0, [0.85, 0.7]),
            fa: loop_keys(1.0, [-0.5, -0.65]),
            "AngleY": loop_keys(1.0, [0.45, 0.35]),
            "AngleZ": loop_keys(1.0, [0.45, 0.3]),
            "BodyAngleZ": loop_keys(1.0, [-0.5, -0.35]),
            b: loop_keys(1.0, [0.35, 0.25]),
            "AngleX": [(0, -0.5), (1.0, -0.5)],
        }, fade=0.3),
        "cheer": Motion("cheer", 1.1, {
            a: [(0, 0.0), (0.3, 0.75), (0.75, 0.6), (1.1, 0.0)],
            fa: [(0, 0.0), (0.3, -0.6), (0.75, -0.4), (1.1, 0.0)],
            "AngleY": [(0, 0.0), (0.25, 0.5), (0.55, -0.15), (1.1, 0.0)],
            "AngleZ": [(0, 0.0), (0.3, 0.35), (1.1, 0.0)],
            "EyeOpen.left": [(0, 1), (0.25, 0.45), (0.7, 0.45), (1.0, 1)],
            "EyeOpen.right": [(0, 1), (0.25, 0.45), (0.7, 0.45), (1.0, 1)],
        }, loop=False, fade=0.18),
        "settle": Motion("settle", 1.6, {
            "AngleY": [(0, 0.0), (0.5, -0.35), (1.6, -0.1)],
            "Breath": [(0, 0.2), (0.6, 1.0), (1.6, 0.0)],
            "AngleZ": [(0, 0.0), (0.7, -0.25), (1.6, 0.0)],
        }, loop=False, fade=0.25),
    }


def band_of(level):
    name = "idle"
    for start, band in LEVELS_PER_BAND:
        if level >= start:
            name = band
    return name


BASE = {"idle": "read", "cruise": "calm", "boost": "brisk", "overdrive": "dash", "redline": "max"}


class Director:
    def __init__(self, rig_params, seed=7, **arms):
        self.motions = library(**arms)
        self.params = rig_params
        self.rng = random.Random(seed)
        self.layers = []
        self.band = None
        self.level = 0
        self.intensity = 0.0
        self.blink_at, self.blink_t = 1.2, None
        self.gaze, self.gaze_target, self.gaze_at = [0.0, 0.0], [0.0, 0.0], 0.8
        self.breath_phase = 0.0
        self.time = 0.0

    def _start(self, name, weight=0.0):
        self.layers.append({"motion": self.motions[name], "t": 0.0, "w": weight, "target": 1.0})
        for layer in self.layers[:-1]:
            if layer["motion"].loop or name != "cheer":
                layer["target"] = 0.0

    def set_level(self, level):
        band = band_of(level)
        if self.band is None:
            self.band = band
            self._start(BASE[band], 1.0)
        elif band != self.band:
            rising = [b for _, b in LEVELS_PER_BAND].index(band) > [b for _, b in LEVELS_PER_BAND].index(self.band)
            self.band = band
            self._start("cheer" if rising else "settle")
            self.pending_base = BASE[band]
        self.level = level

    def update(self, dt):
        self.time += dt
        target_intensity = self.level / 13
        self.intensity += (target_intensity - self.intensity) * min(1.0, dt * 2.5)
        s = self.intensity

        for layer in self.layers:
            layer["t"] += dt
            m = layer["motion"]
            rate = dt / max(m.fade, 1e-3)
            layer["w"] += max(-rate, min(rate, layer["target"] - layer["w"]))
            if not m.loop and layer["t"] >= m.duration - m.fade and layer["target"] > 0:
                layer["target"] = 0.0
                if getattr(self, "pending_base", None):
                    self.layers.append({"motion": self.motions[self.pending_base], "t": 0.0, "w": 0.0, "target": 1.0})
                    for other in self.layers[:-1]:
                        if other["motion"].loop:
                            other["target"] = 0.0
                    self.pending_base = None
        self.layers = [l for l in self.layers if l["w"] > 1e-3 or l["target"] > 0]

        values = {pid: p["default"] for pid, p in self.params.items()}
        total = {}
        for layer in self.layers:
            for pid, v in layer["motion"].sample(layer["t"]).items():
                acc = total.setdefault(pid, [0.0, 0.0])
                acc[0] += v * layer["w"]
                acc[1] += layer["w"]
        for pid, (vsum, wsum) in total.items():
            base = values.get(pid, 0.0)
            w = min(1.0, wsum)
            values[pid] = base * (1 - w) + (vsum / max(wsum, 1e-6)) * w

        period = 4.2 - 2.4 * s
        self.breath_phase += dt / period
        values["Breath"] = max(values.get("Breath", 0.0), 0.5 - 0.5 * math.cos(2 * math.pi * self.breath_phase))

        amp = 0.06 + 0.12 * s
        t = self.time
        values["AngleX"] = values.get("AngleX", 0) + amp * (math.sin(t * 0.83) + 0.5 * math.sin(t * 1.91 + 1.3))
        values["AngleY"] = values.get("AngleY", 0) + amp * 0.7 * math.sin(t * 1.17 + 0.4)
        values["AngleZ"] = values.get("AngleZ", 0) + amp * 0.8 * math.sin(t * 0.61 + 2.1)
        values["BodySway"] = values.get("BodySway", 0) + (0.25 + 0.6 * s) * math.sin(t * (0.9 + 1.6 * s))
        values["Wind"] = min(1.0, s ** 1.2 * 1.05)

        self.gaze_at -= dt
        if self.gaze_at <= 0:
            self.gaze_target = [self.rng.uniform(-0.5, 0.5), self.rng.uniform(-0.35, 0.35)]
            self.gaze_at = self.rng.uniform(0.9, 2.6) * (1.2 - 0.6 * s)
        k = min(1.0, dt * 14)
        self.gaze = [g + (gt - g) * k for g, gt in zip(self.gaze, self.gaze_target)]
        values["EyeBallX"] = max(-1, min(1, values.get("EyeBallX", 0) + 0.45 * self.gaze[0]))
        values["EyeBallY"] = max(-1, min(1, values.get("EyeBallY", 0) + 0.45 * self.gaze[1]))

        openness = 1.0
        self.blink_at -= dt
        if self.blink_t is None and self.blink_at <= 0:
            self.blink_t = 0.0
        if self.blink_t is not None:
            self.blink_t += dt
            bt = self.blink_t
            if bt < 0.07:
                openness = 1 - bt / 0.07
            elif bt < 0.1:
                openness = 0.0
            elif bt < 0.19:
                openness = (bt - 0.1) / 0.09
            else:
                self.blink_t = None
                self.blink_at = self.rng.uniform(2.0, 5.0) if self.rng.random() > 0.15 else 0.25
        for side in ("left", "right"):
            key = f"EyeOpen.{side}"
            if key in values:
                values[key] = min(values[key], openness)
        return values
