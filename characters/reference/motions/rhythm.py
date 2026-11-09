"""Body grooves per speed band on a beat clock: posture, weight shifts, bounce and arm swing.

Each groove maps (beats, seconds) to parameter values. Faster bands get a faster beat, a bigger bounce and wider
swings, so the whole body keeps time with the speed.
"""
import math

BPM = {"idle": 56, "cruise": 84, "boost": 104, "overdrive": 124, "redline": 140}


class Beat:
    def __init__(self, tempo=1.0):
        self.tempo = tempo
        self.bpm, self.phase = BPM["idle"] * tempo, 0.0

    def update(self, dt, band):
        self.bpm += (BPM[band] * self.tempo - self.bpm) * min(1.0, dt * 1.2)
        self.phase += dt * self.bpm / 60


def _up(b):
    """0 on the beat, 1 halfway to the next: one small hop per beat."""
    return abs(math.sin(math.pi * b))


def _hit(b, sharp=4.0):
    """1 right on the beat, quickly falling off."""
    return (1 - abs(math.sin(math.pi * b))) ** sharp


def read(b, t, out, book):
    shift = math.sin(2 * math.pi * t / 7.5)
    return {"AngleY": -0.55 + 0.05 * math.sin(t * 0.9), "AngleZ": -0.35 + 0.05 * math.sin(t * 0.6), "AngleX": -0.22,
            "EyeBallX": -0.4 + 0.25 * math.sin(t * 1.3), "EyeBallY": -0.7,
            f"Arm.{book}": 0.15, f"Forearm.{book}": -0.1,
            "Hip": 0.28 * shift, "BodyAngleZ": -0.15 * shift, "Lean": 0.05 * shift}


def rest(b, t, out, book):
    """The idle groove of a character with nothing to read: head up, a slow look around, weight shifting."""
    shift = math.sin(2 * math.pi * t / 7.5)
    return {"AngleY": -0.08 + 0.05 * math.sin(t * 0.9), "AngleZ": 0.1 * math.sin(t * 0.5), "AngleX": -0.15,
            "EyeBallX": 0.3 * math.sin(t * 0.7), "EyeBallY": -0.1,
            f"Arm.{book}": 0.06, "Hip": 0.25 * shift, "BodyAngleZ": -0.12 * shift, "Lean": 0.04 * shift}


def calm(b, t, out, book):
    s = math.sin(math.pi * b)
    return {"Hip": 0.35 * s, "BodyAngleZ": -0.22 * s, "AngleZ": 0.12 + 0.14 * math.sin(math.pi * b + 0.7),
            "AngleX": -0.3, "AngleY": 0.04 * math.cos(2 * math.pi * b), "EyeBallX": -0.35,
            f"Arm.{out}": 0.08 * s, f"Arm.{book}": 0.04 * s, "SkirtFlare": 0.05}


def brisk(b, t, out, book):
    s, up, hit = math.sin(math.pi * b), _up(b), _hit(b)
    return {"PosY": 0.035 * up, "Squash": 0.1 * up - 0.22 * hit, "Hip": 0.45 * s, "BodyAngleZ": -0.3 * s,
            "AngleZ": -0.25 * s, "AngleY": 0.08 - 0.12 * hit, "AngleX": -0.25,
            f"Arm.{out}": 0.05 + 0.25 * s, f"Forearm.{out}": -0.12 - 0.1 * s, f"Arm.{book}": 0.12 * s,
            f"Shoulder.{out}": 0.25 * s, f"Shoulder.{book}": -0.25 * s, "SkirtFlare": 0.12 + 0.12 * up,
            "FootPoint": 0.3 * up}


def dash(b, t, out, book):
    s, up, hit = math.sin(math.pi * b), _up(b), _hit(b)
    return {"PosY": 0.06 * up, "Squash": 0.15 * up - 0.35 * hit, "Hip": 0.5 * s, "BodyAngleZ": -0.4 * s,
            "Lean": 0.12 * s, "AngleZ": -0.35 * s, "AngleY": 0.2 - 0.15 * hit, "AngleX": -0.35,
            f"Arm.{out}": 0.1 + 0.35 * s, f"Forearm.{out}": -0.3 - 0.2 * s, f"Arm.{book}": 0.08 + 0.2 * s,
            f"Shoulder.{out}": 0.35 * s, f"Shoulder.{book}": -0.35 * s, "SkirtFlare": 0.25 + 0.15 * up,
            "FootPoint": 0.5 * up, "FootLift": 0.25 * up}


def peak(b, t, out, book):
    s, up, hit = math.sin(math.pi * b), _up(b), _hit(b)
    return {"PosY": 0.1 * up, "Squash": 0.2 * up - 0.45 * hit, "Hip": 0.35 * s, "BodyAngleZ": -0.35 * s,
            "Lean": 0.1 * s, "AngleZ": 0.35 * s, "AngleY": 0.35 - 0.2 * hit, "AngleX": -0.45,
            f"Arm.{out}": 0.4 + 0.12 * s, f"Forearm.{out}": -0.55 - 0.1 * s, f"Arm.{book}": 0.25 + 0.08 * s,
            f"Shoulder.{out}": 0.35 + 0.2 * s, f"Shoulder.{book}": 0.25 - 0.2 * s, "SkirtFlare": 0.4 + 0.25 * up,
            "FootPoint": 0.8 * up, "FootLift": 0.4 * up}


GROOVES = {"idle": read, "cruise": calm, "boost": brisk, "overdrive": dash, "redline": peak}

# Groove parameters each profile multiplier scales (spec/CHARACTERS.md, Motion profile).
BOUNCE = ("PosY", "Squash", "FootPoint", "FootLift", "SkirtFlare")
SWAY = ("Hip", "BodyAngleZ", "Lean", "AngleZ")
ENERGY = ("Arm.", "Forearm.", "Shoulder.")


def groove_scale(pid, profile):
    if pid in BOUNCE:
        return profile.bounce
    if pid in SWAY:
        return profile.sway
    if pid.startswith(ENERGY):
        return profile.energy
    return 1.0


def groove_for(band, profile):
    return rest if band == "idle" and profile.idle == "rest" else GROOVES[band]
