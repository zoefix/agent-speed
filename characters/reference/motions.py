"""Motions and the director that turns speed into motion.

Three layers: a body loop per speed band (posture, sway, rhythm), one-shot gestures that swap in pose variants
(wave, peace sign, fist pump, ...) and an expression that changes while the eyes are closed in a blink. Procedural
breathing, glances, blinks and wind run on top; their tempo and strength follow the speed.
"""
import math
import random

LEVELS_PER_BAND = [(0, "idle"), (1, "cruise"), (5, "boost"), (9, "overdrive"), (12, "redline")]
BANDS = [b for _, b in LEVELS_PER_BAND]


def ease(t):
    return t * t * (3 - 2 * t)


class Motion:
    def __init__(self, name, duration, curves, loop=True, fade=0.4, expression=None, talk=False):
        self.name, self.duration, self.loop, self.fade = name, duration, loop, fade
        self.expression, self.talk = expression, talk
        self.curves = {p: sorted(keys) for p, keys in curves.items()}

    def sample(self, t):
        t = t % self.duration if self.loop else min(t, self.duration)
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


def oscillate(start, end, period, amplitude, base=0.0, phase=0.0):
    keys, t = [], start
    step = period / 4
    k = 0
    while t < end - 1e-6:
        keys.append((t, base + amplitude * math.sin(phase + k * math.pi / 2)))
        t += step
        k += 1
    keys.append((end, base))
    return keys


def body_library(arm_out="right", arm_book="left"):
    a, b = f"Arm.{arm_out}", f"Arm.{arm_book}"
    fa, fb = f"Forearm.{arm_out}", f"Forearm.{arm_book}"
    return {
        "read": Motion("read", 6.0, {
            "AngleY": loop_keys(6.0, [-0.55, -0.6, -0.5, -0.6]),
            "AngleZ": loop_keys(6.0, [-0.35, -0.4, -0.3, -0.38]),
            "AngleX": loop_keys(6.0, [-0.25, -0.2, -0.3, -0.22]),
            "EyeBallX": loop_keys(6.0, [-0.5, -0.2, -0.55, -0.15]),
            "EyeBallY": [(0, -0.7), (6.0, -0.7)],
            b: [(0, 0.15), (6.0, 0.15)], fb: [(0, -0.1), (6.0, -0.1)],
        }),
        "calm": Motion("calm", 5.0, {
            "AngleX": loop_keys(5.0, [-0.35, -0.25, -0.4, -0.3]),
            "AngleY": loop_keys(5.0, [0.0, 0.05, -0.05, 0.02]),
            "AngleZ": loop_keys(5.0, [0.1, 0.2, 0.05, 0.15]),
            "EyeBallX": [(0, -0.45), (5.0, -0.45)],
        }),
        "brisk": Motion("brisk", 2.4, {
            "AngleZ": loop_keys(2.4, [-0.25, 0.25]), "BodyAngleZ": loop_keys(2.4, [0.2, -0.2]),
            a: loop_keys(2.4, [0.28, -0.18]), fa: loop_keys(2.4, [0.05, -0.25]),
            "AngleY": loop_keys(2.4, [0.1, 0.0]), "AngleX": [(0, -0.3), (2.4, -0.3)],
        }, fade=0.35),
        "dash": Motion("dash", 1.4, {
            "AngleZ": loop_keys(1.4, [-0.4, 0.35]), "BodyAngleZ": loop_keys(1.4, [0.45, -0.1]),
            a: loop_keys(1.4, [0.5, -0.25]), fa: loop_keys(1.4, [0.1, -0.45]), b: loop_keys(1.4, [0.25, 0.05]),
            "AngleY": [(0, 0.2), (1.4, 0.2)], "AngleX": [(0, -0.45), (1.4, -0.45)],
        }, fade=0.3),
        "max": Motion("max", 1.0, {
            a: loop_keys(1.0, [0.85, 0.7]), fa: loop_keys(1.0, [-0.5, -0.65]),
            "AngleY": loop_keys(1.0, [0.45, 0.35]), "AngleZ": loop_keys(1.0, [0.45, 0.3]),
            "BodyAngleZ": loop_keys(1.0, [-0.5, -0.35]), b: loop_keys(1.0, [0.35, 0.25]),
            "AngleX": [(0, -0.5), (1.0, -0.5)],
        }, fade=0.3),
    }


BODY = {"idle": "read", "cruise": "calm", "boost": "brisk", "overdrive": "dash", "redline": "max"}

# gesture id -> (hold seconds, flourish, expression)
GESTURES = {
    "wave": (1.6, "wave", "smile"),
    "peace": (1.3, "bob", "happy"),
    "fist": (1.2, "pump", "determined"),
    "idea": (1.4, "pop", "surprised"),
    "chin": (2.4, "think", None),
    "hairtuck": (1.6, "slow", "smile"),
    "present": (1.8, "offer", "talk"),
    "hip": (1.8, "lean", "determined"),
    "hug": (2.4, "sway", "smile"),
    "raise": (1.4, "pump", "happy"),
    "open": (3.4, "read", None),
}
POOLS = {
    "idle": ["open", "chin", "hairtuck", "hug", "yawn"],
    "cruise": ["wave", "hug", "present", "hairtuck", "hum"],
    "boost": ["idea", "peace", "hip", "wave", "hum"],
    "overdrive": ["fist", "present", "hip", "idea"],
    "redline": ["fist", "raise", "peace"],
}
CELEBRATE = ["peace", "fist", "raise", "wave"]
BAND_FACE = {"idle": None, "cruise": "smile", "boost": "smile", "overdrive": "determined", "redline": "happy"}


def arm_gesture(meta, hold, flourish, expression):
    var, arm, fore = meta["param"], meta["arm"], meta["forearm"]
    t1, t2 = 0.2, 0.27
    t3 = t2 + 0.3
    t4 = t3 + hold
    t5 = t4 + 0.22
    t6 = t5 + 0.07
    t7 = t6 + 0.32
    curves = {var: [(0, 0), (t1, 0), (t2, 1), (t5, 1), (t6, 0), (t7, 0)]}
    for pid, lead in meta["lead"].items():
        curves[pid] = [(0, 0), (t1, lead), (t6, lead), (t7, 0)]
    for pid, approach in meta["approach"].items():
        curves[pid] = [(0, approach), (t1, approach), (t3, 0), (t4, 0), (t5, approach), (t7, approach)]
    curves.setdefault(arm, [(0, 0), (t1, 0), (t3, 0), (t4, 0), (t5, 0), (t7, 0)])
    curves.setdefault(fore, [(0, 0), (t1, 0), (t3, 0), (t4, 0), (t5, 0), (t7, 0)])

    def during(pid, keys):
        curves[pid] = curves[pid][:3] + keys + curves[pid][-3:]

    head = {}
    if flourish == "wave":
        during(fore, oscillate(t3, t4, 0.42, 0.45))
        head = {"AngleZ": [(0, 0), (t3, 0.25), (t4, 0.25), (t7, 0)], "AngleX": [(0, 0), (t3, -0.3), (t4, -0.3), (t7, 0)]}
    elif flourish == "bob":
        during(arm, oscillate(t3, t4, 0.5, 0.06))
        head = {"AngleZ": [(0, 0), (t3, 0.45), (t4, 0.45), (t7, 0)]}
    elif flourish == "pump":
        during(arm, oscillate(t3, t4, 0.34, 0.12))
        head = {"AngleY": [(0, 0), (t3, 0.4), (t4, 0.4), (t7, 0)], "BodyAngleZ": [(0, 0), (t3, -0.4), (t4, -0.4), (t7, 0)]}
    elif flourish == "pop":
        during(fore, [(t3 + 0.12, -0.2), (t3 + 0.3, 0)])
        head = {"AngleY": [(0, 0), (t3, 0.5), (t4, 0.45), (t7, 0)], "EyeBallY": [(0, 0), (t3, -0.7), (t4, -0.7), (t7, 0)]}
    elif flourish == "think":
        head = {"AngleZ": [(0, 0), (t3, 0.35), (t4, 0.4), (t7, 0)], "AngleY": [(0, 0), (t3, 0.25), (t4, 0.2), (t7, 0)],
                "EyeBallY": [(0, 0), (t3, -0.8), (t4, -0.8), (t7, 0)], "EyeBallX": [(0, 0), (t3, 0.5), (t4, 0.6), (t7, 0)]}
    elif flourish == "slow":
        during(fore, [(t3 + hold * 0.5, 0.15)])
        head = {"AngleZ": [(0, 0), (t3, 0.4), (t4, 0.4), (t7, 0)], "EyeBallX": [(0, 0), (t3, 0.4), (t4, 0.4), (t7, 0)]}
    elif flourish == "offer":
        during(arm, [(t3 + hold * 0.5, -0.05)])
        head = {"AngleX": [(0, 0), (t3, 0.45), (t4, 0.45), (t7, 0)], "EyeBallX": [(0, 0), (t3, 0.7), (t4, 0.7), (t7, 0)]}
    elif flourish == "lean":
        head = {"BodyAngleZ": [(0, 0), (t3, 0.55), (t4, 0.55), (t7, 0)], "AngleZ": [(0, 0), (t3, -0.3), (t4, -0.3), (t7, 0)]}
    elif flourish == "sway":
        head = {"AngleZ": [(0, 0), (t3, -0.3), (t3 + hold / 2, 0.2), (t4, -0.3), (t7, 0)],
                "BodyAngleZ": oscillate(0, t7, 1.6, 0.25)}
    elif flourish == "read":
        head = {"AngleY": [(0, 0), (t3, -0.6), (t4, -0.6), (t7, 0)],
                "EyeBallY": [(0, 0), (t3, 0.6), (t4, 0.6), (t7, 0)],
                "EyeBallX": [(0, 0), (t3, -0.6)] + oscillate(t3, t4, 1.1, 0.5, base=0.0) + [(t7, 0)]}
    curves.update(head)
    return Motion(meta["id"], t7, curves, loop=False, fade=0.12, expression=expression)


def face_gesture(name, duration=2.4):
    if name == "yawn":
        return Motion("yawn", duration, {
            "AngleY": [(0, 0), (0.5, 0.45), (1.6, 0.4), (duration, 0)],
            "AngleZ": [(0, 0), (0.6, -0.35), (duration, 0)],
            "Breath": [(0, 0), (0.7, 1.0), (1.8, 0.2), (duration, 0)],
        }, loop=False, fade=0.25, expression="sleepy")
    return Motion("hum", duration + 1.0, {
        "AngleZ": oscillate(0, duration + 1.0, 1.2, 0.3),
        "AngleX": [(0, 0), (0.4, -0.25), (duration + 1.0, 0)],
    }, loop=False, fade=0.25, expression="smile", talk=True)


def band_of(level):
    name = "idle"
    for start, band in LEVELS_PER_BAND:
        if level >= start:
            name = band
    return name


class Director:
    def __init__(self, rig_params, variants=(), seed=7, arm_out="right", arm_book="left"):
        self.params = rig_params
        self.body_motions = body_library(arm_out, arm_book)
        self.meta = {v["id"]: v for v in variants}
        self.faces = {k: v for k, v in self.meta.items() if v["kind"] == "face"}
        self.rng = random.Random(seed)
        self.body = []
        self.gesture = None
        self.next_gesture = 2.5
        self.last_gesture = None
        self.band, self.level = None, 0
        self.intensity = 0.0
        self.event_face, self.event_until = None, 0.0
        self.face, self.face_weights = None, {k: 0.0 for k in self.faces}
        self.mouth_weights = {k: 0.0 for k in self.faces}
        self.talk_open, self.talk_next = False, 0.0
        self.blink_at, self.blink_t, self.blink_forced = 1.2, None, False
        self.gaze, self.gaze_target, self.gaze_at = [0.0, 0.0], [0.0, 0.0], 0.8
        self.breath_phase = 0.0
        self.time = 0.0

    def _play_body(self, name):
        for layer in self.body:
            layer["target"] = 0.0
        self.body.append({"motion": self.body_motions[name], "t": 0.0, "w": 0.0 if self.body else 1.0, "target": 1.0})

    def _gesture(self, gid):
        if gid in ("yawn", "hum"):
            if gid == "yawn" and "sleepy" not in self.faces:
                return None
            if gid == "hum" and "talk" not in self.faces:
                return None
            return face_gesture(gid)
        if gid not in self.meta or self.meta[gid]["kind"] != "arm":
            return None
        hold, flourish, expression = GESTURES[gid]
        return arm_gesture(self.meta[gid], hold, flourish, expression)

    def _start_gesture(self, gid):
        motion = self._gesture(gid)
        if motion is None:
            return False
        self.gesture = {"motion": motion, "t": 0.0, "w": 0.0}
        self.last_gesture = gid
        return True

    def set_level(self, level):
        band = band_of(level)
        if self.band is None:
            self.band = band
            self._play_body(BODY[band])
        elif band != self.band:
            rising = BANDS.index(band) > BANDS.index(self.band)
            self.band = band
            self._play_body(BODY[band])
            if rising:
                options = [g for g in CELEBRATE if g in self.meta] or [None]
                choice = self.rng.choice(options)
                if choice and (self.gesture is None or self.gesture["t"] > 0.5 * self.gesture["motion"].duration):
                    self._start_gesture(choice)
                self.event_face, self.event_until = ("surprised" if band == "redline" else "happy"), self.time + 1.2
            else:
                self.event_face, self.event_until = "smile", self.time + 1.5
            self.next_gesture = self.time + 2.0
        self.level = level

    def _blend_body(self, dt, values):
        for layer in self.body:
            layer["t"] += dt
            fade = layer["motion"].fade
            layer["w"] += max(-dt / fade, min(dt / fade, layer["target"] - layer["w"]))
        self.body = [layer for layer in self.body if layer["w"] > 1e-3 or layer["target"] > 0]
        total = {}
        for layer in self.body:
            for pid, v in layer["motion"].sample(layer["t"]).items():
                acc = total.setdefault(pid, [0.0, 0.0])
                acc[0] += v * layer["w"]
                acc[1] += layer["w"]
        for pid, (vsum, wsum) in total.items():
            w = min(1.0, wsum)
            values[pid] = values.get(pid, 0.0) * (1 - w) + vsum / max(wsum, 1e-6) * w

    def _apply_gesture(self, dt, values):
        if self.gesture is None:
            if self.time >= self.next_gesture:
                pool = [g for g in POOLS[self.band] if g != self.last_gesture]
                self.rng.shuffle(pool)
                for gid in pool:
                    if self._start_gesture(gid):
                        break
                self.next_gesture = self.time + 1.0
            return None
        g = self.gesture
        g["t"] += dt
        m = g["motion"]
        fade = m.fade
        g["w"] = min(1.0, g["t"] / fade, max(0.0, (m.duration - g["t"]) / fade))
        for pid, v in m.sample(g["t"]).items():
            hard = pid.startswith(("Var.", "Lift."))
            values[pid] = v if hard else values.get(pid, 0.0) * (1 - g["w"]) + v * g["w"]
        if g["t"] >= m.duration:
            self.gesture = None
            gap = (3.2, 6.0) if self.band == "idle" else (1.4, 3.5) if self.band in ("cruise", "boost") else (0.8, 2.2)
            self.next_gesture = self.time + self.rng.uniform(*gap)
            return None
        return m

    def update(self, dt):
        self.time += dt
        self.intensity += (self.level / 13 - self.intensity) * min(1.0, dt * 2.5)
        s, t = self.intensity, self.time
        values = {pid: p["default"] for pid, p in self.params.items()}
        self._blend_body(dt, values)
        gesture = self._apply_gesture(dt, values)

        self.breath_phase += dt / (4.2 - 2.4 * s)
        values["Breath"] = max(values.get("Breath", 0.0), 0.5 - 0.5 * math.cos(2 * math.pi * self.breath_phase))
        amp = 0.06 + 0.12 * s
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
        values["EyeBallX"] = max(-1, min(1, values.get("EyeBallX", 0) + 0.35 * self.gaze[0]))
        values["EyeBallY"] = max(-1, min(1, values.get("EyeBallY", 0) + 0.35 * self.gaze[1]))

        wanted = self.event_face if t < self.event_until else (gesture.expression if gesture and gesture.expression
                                                                 else BAND_FACE[self.band])
        if wanted not in self.faces:
            wanted = None
        openness = self._blink(dt, force=wanted != self.face)
        if openness <= 0.02 and wanted != self.face:
            self.face = wanted
        closed_face = self.face is not None and self.faces[self.face].get("eyesClosed")
        if closed_face:
            openness = 1.0
        for fid in self.faces:
            target = 1.0 if fid == self.face else 0.0
            self.face_weights[fid] += max(-dt / 0.05, min(dt / 0.05, target - self.face_weights[fid]))
            values[self.faces[fid]["param"]] = self.face_weights[fid]

        mouth = self.face
        if gesture is not None and gesture.talk and "talk" in self.faces:
            self.talk_next -= dt
            if self.talk_next <= 0:
                self.talk_open = not self.talk_open
                self.talk_next = self.rng.uniform(0.07, 0.16) if self.talk_open else self.rng.uniform(0.05, 0.22)
            mouth = "talk" if self.talk_open else self.face
        for fid in self.faces:
            target = 1.0 if fid == mouth else 0.0
            self.mouth_weights[fid] += max(-dt / 0.04, min(dt / 0.04, target - self.mouth_weights[fid]))
            values[self.faces[fid]["mouth"]] = self.mouth_weights[fid]

        for side in ("left", "right"):
            key = f"EyeOpen.{side}"
            if key in values:
                values[key] = min(values[key], openness)
        return values

    def _blink(self, dt, force=False):
        self.blink_at -= dt
        if self.blink_t is None and (self.blink_at <= 0 or force):
            self.blink_t = 0.0
        if self.blink_t is None:
            return 1.0
        self.blink_t += dt
        bt = self.blink_t
        if bt < 0.07:
            return 1 - bt / 0.07
        if bt < 0.11:
            return 0.0
        if bt < 0.2:
            return (bt - 0.11) / 0.09
        self.blink_t = None
        self.blink_at = self.rng.uniform(2.0, 5.0) if self.rng.random() > 0.15 else 0.25
        return 1.0
