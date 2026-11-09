"""The director turns speed into motion.

Layers, bottom to top: a groove per speed band on a beat clock; one action at a time (an arm gesture, a two-arm combo,
a head or face gesture, a whole-body move, or a jump with a combo); breathing, glances, blinks and wind; then springs
give everything weight. Expressions change while the eyes are closed in a blink. Side steps move the character
around its spot, away from the orb, and it stays where it lands.
"""
import math

from ..rng import Rng
from .curves import merge
from .gestures import COMBOS, FACE_GESTURES, GESTURES, HEAD_GESTURES, arm_gesture, combo, face_gesture, head_gesture
from .hands import HandActor
from .moves import MOVES, PROFILE_MOVES, jump, peek, pounce, sidestep
from .profile import ARM_PARAMS, AT_MOUTH, HEAD_PARAMS, SEATED_FREEZE, TWO_HANDED, MotionProfile
from .rhythm import Beat, groove_for, groove_scale
from .springs import Springs

LEVELS_PER_BAND = [(0, "idle"), (1, "cruise"), (5, "boost"), (9, "overdrive"), (12, "redline")]
BANDS = [b for _, b in LEVELS_PER_BAND]
HARD = ("Var.", "Lift.", "Bend.", "Spin", "PosX")
POOLS = {
    "idle": ["open", "chin", "hairtuck", "hug", "yawn", "stretch", "lookaround", "tilt", "foottap", "ponder", "step",
             "bow"],
    "cruise": ["wave", "hug", "present", "hairtuck", "hum", "nod", "tilt", "giggle", "sway", "tiptoe", "curtsy", "step",
               "peek", "cuddle"],
    "boost": ["idea", "peace", "hip", "wave", "hop", "sway", "tiptoe", "step", "spin", "giggle", "doubletake", "hooray",
              "eureka", "hops"],
    "overdrive": ["fist", "present", "hip", "idea", "hops", "run", "spin", "step", "doubletake", "cheer", "proud", "hop"],
    "redline": ["fist", "raise", "peace", "jumpjoy", "run", "spin", "hops", "cheer", "hooray", "jump"],
}
CELEBRATE = ["jumpjoy", "spin", "cheer", "hop"]
BAND_FACE = {"idle": None, "cruise": "smile", "boost": "smile", "overdrive": "determined", "redline": "happy"}
GAPS = {"idle": (2.4, 4.4), "cruise": (0.9, 2.2), "boost": (0.7, 1.8), "overdrive": (0.4, 1.3), "redline": (0.3, 1.0)}
STAGE = 0.55


def band_of(level):
    name = "idle"
    for start, band in LEVELS_PER_BAND:
        if level >= start:
            name = band
    return name


class Director:
    def __init__(self, rig_params, variants=(), seed=7, arm_out="right", arm_book="left", orb=-1.0, celebrate=True,
                 profile=None):
        self.params = rig_params
        self.out, self.book, self.orb, self.celebrate = arm_out, arm_book, orb, celebrate
        self.profile = profile if isinstance(profile, MotionProfile) else MotionProfile(profile)
        self.meta = {v["id"]: v for v in variants}
        self.faces = {k: v for k, v in self.meta.items() if v["kind"] == "face"}
        self.rng = Rng(seed)
        self.springs = Springs()
        self.beat = Beat(self.profile.tempo)
        self.grooves = []
        self.gesture = None
        self.next_gesture = 2.0
        self.last_gesture = None
        self.recent = []
        self.stage_x = 0.0
        self.events = []
        self.band, self.level = None, 0
        self.intensity = 0.0
        self.event_face, self.event_until = None, 0.0
        self.face, self.face_weights = None, {k: 0.0 for k in self.faces}
        self.mouth_weights = {k: 0.0 for k in self.faces}
        self.talk_open, self.talk_next = False, 0.0
        self.blink_at, self.blink_t = 1.2, None
        self.gaze, self.gaze_target, self.gaze_at = [0.0, 0.0], [0.0, 0.0], 0.8
        self.breath_phase = 0.0
        self.time = 0.0
        self.hands = [HandActor(side, side == arm_book, self.rng, phase=k * 1.7, sets=self.profile.hands)
                      for k, side in enumerate(("left", "right")) if f"Finger.{side}.0" in rig_params]
        self.pose_band = "idle"
        for hand in self.hands:
            hand.set_mode(self.profile.pose_hands.get(self.pose_band))
        self.arm_params = [pid for pid in rig_params if pid.startswith(ARM_PARAMS)]
        self.arms_held = self.profile.pose_hands.get(self.pose_band) in TWO_HANDED
        self.head_held = self.profile.pose_hands.get(self.pose_band) in AT_MOUTH

    def set_sides(self, arm_out, arm_book, pose_band=None):
        """Which arm swings free and which holds the object, for a new pose (of `pose_band`, for its hand set)."""
        self.out, self.book = arm_out, arm_book
        if pose_band is not None:
            self.pose_band = pose_band
        for hand in self.hands:
            hand.holds = hand.side == arm_book
            hand.set_mode(self.profile.pose_hands.get(self.pose_band))
        self.arms_held = self.profile.pose_hands.get(self.pose_band) in TWO_HANDED
        self.head_held = self.profile.pose_hands.get(self.pose_band) in AT_MOUTH
        for layer in self.grooves:
            layer["target"] = 0.0
        self.grooves.append({"fn": groove_for(self.band, self.profile), "w": 0.0, "target": 1.0})

    def _can(self, *pids):
        return all(p in self.params for p in pids)

    def _arm(self, gid):
        m = self.meta.get(gid)
        return m if m is not None and m["kind"] == "arm" else None

    def _gesture(self, gid):
        out, book = self.out, self.book
        if gid in GESTURES:
            m = self._arm(gid)
            if m is None:
                return None
            hold, flourish, expression, toes = GESTURES[gid]
            return arm_gesture(m, hold, flourish, expression, tiptoe=toes and self._can("FootPoint"))
        if gid in COMBOS:
            a, b, hold, expression = COMBOS[gid]
            if self._arm(a) is None or self._arm(b) is None:
                return None
            return combo(gid, self._arm(a), self._arm(b), hold, expression)
        if gid == "jumpjoy":
            if not self._can("PosY", "Squash"):
                return None
            cheer = self._gesture("cheer")
            body = jump(out, book, arms=cheer is None)
            return merge("jumpjoy", [cheer, body], delays=[0.0, 0.2], expression="happy") if cheer else body
        if gid == "step":
            if not self._can("PosX", "PosY"):
                return None
            away = -self.orb * STAGE
            targets = [x for x in (0.0, 0.45 * away, away) if abs(x - self.stage_x) > 0.1]
            return sidestep(out, book, self.rng.choice(targets) - self.stage_x)
        if gid == "peek":
            return peek(out, book, self.orb) if self._can("Lean") else None
        if gid == "pounce":
            return pounce(out, book, self.orb) if self._can("PosY", "Squash") else None
        if gid in PROFILE_MOVES:
            make, needs = PROFILE_MOVES[gid]
            return make(out, book) if any(p in self.params for p in needs) else None
        if gid in MOVES:
            return MOVES[gid](out, book) if self._can("PosY", "Squash") else None
        if gid in HEAD_GESTURES:
            return head_gesture(gid)
        if gid in FACE_GESTURES:
            needs = "sleepy" if gid == "yawn" else "talk"
            return face_gesture(gid) if needs in self.faces or gid in self.profile.signature else None
        return None

    def _start_gesture(self, gid):
        motion = self._gesture(gid) if self.profile.allows(gid) else None
        if motion is None:
            return False
        self.gesture = {"motion": motion, "t": 0.0, "w": 0.0, "fired": 0}
        self.last_gesture = gid
        self.recent = (self.recent + [gid])[-3:]
        for pid, v in motion.starts().items():
            if "@" in pid:
                self.springs.snap(pid, v)
        return True

    def _idle_share(self, pid):
        g = self.gesture
        return 1.0 - g["w"] if g is not None and pid in g["motion"].curves else 1.0

    def _clamp(self, pid, v):
        p = self.params[pid]
        return max(p["min"], min(p["max"], v))

    def _play_groove(self, band):
        for layer in self.grooves:
            layer["target"] = 0.0
        self.grooves.append({"fn": groove_for(band, self.profile), "w": 0.0 if self.grooves else 1.0, "target": 1.0})

    def set_level(self, level):
        band = band_of(level)
        if self.band is None:
            self.band = band
            self._play_groove(band)
        elif band != self.band:
            rising = BANDS.index(band) > BANDS.index(self.band)
            self.band = band
            self._play_groove(band)
            if rising:
                if self.celebrate and (self.gesture is None or self.gesture["t"] > 0.5 * self.gesture["motion"].duration):
                    for gid in self.rng.sample(CELEBRATE, len(CELEBRATE)):
                        if self._start_gesture(gid):
                            break
                self.event_face, self.event_until = ("surprised" if band == "redline" else "happy"), self.time + 1.2
            else:
                self.event_face, self.event_until = "smile", self.time + 1.5
            self.next_gesture = self.time + 1.5
        self.level = level

    def _blend_grooves(self, dt, values):
        for layer in self.grooves:
            layer["w"] += max(-dt / 0.5, min(dt / 0.5, layer["target"] - layer["w"]))
        self.grooves = [layer for layer in self.grooves if layer["w"] > 1e-3 or layer["target"] > 0]
        total = {}
        for layer in self.grooves:
            for pid, v in layer["fn"](self.beat.phase, self.time, self.out, self.book).items():
                v *= groove_scale(pid, self.profile)
                acc = total.setdefault(pid, [0.0, 0.0])
                acc[0] += v * layer["w"]
                acc[1] += layer["w"]
        for pid, (vsum, wsum) in total.items():
            w = min(1.0, wsum)
            values[pid] = values.get(pid, 0.0) * (1 - w) + vsum / max(wsum, 1e-6) * w

    def _apply_gesture(self, dt, values):
        if self.gesture is None:
            if self.time >= self.next_gesture:
                pool = [g for g in self.profile.pool(POOLS[self.band], self.band) if g not in self.recent]
                self.rng.shuffle(pool)
                for gid in pool:
                    if self._start_gesture(gid):
                        break
                self.next_gesture = self.time + 1.0
            return None
        g = self.gesture
        g["t"] += dt
        m = g["motion"]
        g["w"] = min(1.0, g["t"] / m.fade, max(0.0, (m.duration - g["t"]) / m.fade))
        for pid, v in m.sample(g["t"]).items():
            values[pid] = v if pid.startswith(HARD) else values.get(pid, 0.0) * (1 - g["w"]) + v * g["w"]
        while g["fired"] < len(m.events) and m.events[g["fired"]][0] <= g["t"]:
            self.events.append(m.events[g["fired"]][1])
            g["fired"] += 1
        if g["t"] >= m.duration:
            self.stage_x += m.travel
            self.gesture = None
            lo, hi = GAPS[self.band]
            self.next_gesture = self.time + self.rng.uniform(lo * self.profile.gap, hi * self.profile.gap)
            return None
        return m

    def update(self, dt):
        self.time += dt
        self.intensity += (self.level / 13 - self.intensity) * min(1.0, dt * 2.5)
        s, t = self.intensity, self.time
        self.beat.update(dt, self.band)
        values = {pid: p["default"] for pid, p in self.params.items()}
        self._blend_grooves(dt, values)
        gesture = self._apply_gesture(dt, values)
        values["PosX"] = values.get("PosX", 0.0) + self.stage_x
        if self.arms_held:
            for pid in self.arm_params:
                values[pid] = self.params[pid]["default"]
        for hand in self.hands:
            side = hand.side
            arm = sum(values.get(f"{k}.{side}", 0.0) for k in ("Arm", "Forearm", "Lift", "Bend"))
            for pid, v in hand.update(dt, self.time, self.band, 60 / self.beat.bpm, arm).items():
                values[pid] = values.get(pid, 0.0) + v

        self.breath_phase += dt / (4.2 - 2.4 * s)
        values["Breath"] = max(values.get("Breath", 0.0), 0.5 - 0.5 * math.cos(2 * math.pi * self.breath_phase))
        amp = 0.06 + 0.12 * s
        values["AngleX"] = values.get("AngleX", 0) + amp * (math.sin(t * 0.83) + 0.5 * math.sin(t * 1.91 + 1.3))
        values["AngleY"] = values.get("AngleY", 0) + amp * 0.7 * math.sin(t * 1.17 + 0.4)
        values["AngleZ"] = values.get("AngleZ", 0) + amp * 0.8 * math.sin(t * 0.61 + 2.1)
        values["BodySway"] = values.get("BodySway", 0) + (0.15 + 0.3 * s) * math.sin(t * (0.9 + 1.6 * s))
        values["Wind"] = min(1.0, s ** 1.2 * 1.05)
        # The idle swish and flutter give way to an action that moves the tail or the wings.
        idle = {"TailSwish": (0.18 + 0.3 * s) * self.profile.sway * math.sin(math.pi * self.beat.phase + 0.5),
                "WingFlap": 0.12 * math.sin(2 * math.pi * self.breath_phase)}
        for pid, v in idle.items():
            if pid in self.params:
                values[pid] = self._clamp(pid, values.get(pid, 0.0) + self._idle_share(pid) * v)
        if self.profile.seated:
            for pid in SEATED_FREEZE:
                if pid in self.params:
                    values[pid] = self.params[pid]["default"]
        if self.head_held:
            for pid in HEAD_PARAMS:
                if pid in self.params:
                    values[pid] = self.params[pid]["default"]
        self.springs.apply(dt, values)

        self.gaze_at -= dt
        if self.gaze_at <= 0:
            self.gaze_target = [self.rng.uniform(-0.5, 0.5), self.rng.uniform(-0.35, 0.35)]
            self.gaze_at = self.rng.uniform(0.9, 2.6) * (1.2 - 0.6 * s)
        k = min(1.0, dt * 14)
        self.gaze = [g + (gt - g) * k for g, gt in zip(self.gaze, self.gaze_target)]
        values["EyeBallX"] = max(-1, min(1, values.get("EyeBallX", 0) + 0.35 * self.gaze[0]))
        values["EyeBallY"] = max(-1, min(1, values.get("EyeBallY", 0) + 0.35 * self.gaze[1]))

        self._expression(dt, values, gesture)
        return {pid: v for pid, v in values.items() if pid in self.params}

    def _expression(self, dt, values, gesture):
        wanted = self.event_face if self.time < self.event_until else (
            gesture.expression if gesture and gesture.expression else BAND_FACE[self.band])
        if wanted not in self.faces:
            wanted = None
        openness = self._blink(dt, force=wanted != self.face)
        if openness <= 0.02 and wanted != self.face:
            self.face = wanted
        if self.face is not None and self.faces[self.face].get("eyesClosed"):
            openness = 1.0
        for fid in self.faces:
            target = 1.0 if fid == self.face else 0.0
            self.face_weights[fid] += max(-dt / 0.05, min(dt / 0.05, target - self.face_weights[fid]))
            values[self.faces[fid]["param"]] = self.face_weights[fid]

        mouth = self.face
        if gesture is not None and gesture.talk and self.profile.talk and "talk" in self.faces:
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
