"""Whole-body moves: hops, a jump, a paper-doll spin, side steps, a curtsy, tiptoes, foot taps, a sway dance, running
in place, a stretch, a bow and a peek at the orb.

Signs follow rig/params.py: PosY and Squash up are positive, Lean and PosX positive go to the image right, Arm.* positive
swings an arm outward. `out` is the free arm, `book` the arm holding an object, `orb` is -1 or 1 toward the orb.
"""
import math

from .curves import Motion, ballistic, oscillate


def _both(prefix, keys):
    return {f"{prefix}.left": keys, f"{prefix}.right": keys}


def _swing(d, period, amplitude, base=0.0, phase=0.0, ease=0.15):
    """An oscillation around `base` that eases in from rest and back to rest."""
    return [(0, 0)] + oscillate(ease, d - ease, period, amplitude, base=base, phase=phase) + [(d, 0)]


def hop(out, book, height=0.3, air=0.36, name="hop", expression="happy", arms=True):
    t0, t1 = 0.14, 0.22
    t2 = t1 + air
    t3 = t2 + 0.08
    t4 = t3 + 0.26
    apex = t1 + air / 2
    curves = {
        "PosY": [(0, 0)] + ballistic(t1, air, height) + [(t4, 0)],
        "Squash": [(0, 0), (t0, -0.55), (t1, 0.45), (apex, 0.08), (t2 - 0.02, 0.3), (t3, -0.6), (t3 + 0.1, 0.12),
                   (t4, 0)],
        "SkirtFlare": [(0, 0), (t1, 0.05), (apex, 0.6), (t2, 0.45), (t3, 0.75), (t4, 0)],
        "FootPoint": [(0, 0), (t1, 0.2), (t1 + 0.08, 1.0), (t2 - 0.06, 0.9), (t2, 0), (t4, 0)],
        "FootLift": [(0, 0), (t1, 0), (t1 + 0.1, 0.5), (t2 - 0.08, 0.3), (t2, 0), (t4, 0)],
        "AngleY": [(0, 0), (t0, -0.3), (t1 + 0.05, 0.35), (t2, 0.0), (t3, -0.35), (t4, 0)],
        **_both("Shoulder", [(0, 0), (t0, -0.4), (t1 + 0.05, 0.7), (t2, 0.2), (t3, -0.3), (t4, 0)]),
    }
    if arms:
        curves[f"Arm.{out}"] = [(0, 0), (t0, -0.1), (t1 + 0.08, 0.35), (t2, 0.15), (t3, 0), (t4, 0)]
        curves[f"Arm.{book}"] = [(0, 0), (t0, -0.05), (t1 + 0.08, 0.25), (t2, 0.1), (t3, 0), (t4, 0)]
    return Motion(name, t4, curves, expression=expression, events=[(t1, "petals")])


def jump(out, book, arms=True):
    return hop(out, book, height=0.45, air=0.48, name="jump", arms=arms)


def hops(out, book, count=3, height=0.22, air=0.3, name="hops"):
    keys = {p: [(0, 0)] for p in ("PosY", "Squash", "FootPoint", "SkirtFlare", "AngleY", "Hip", f"Arm.{out}",
                                  f"Arm.{book}")}
    t = 0.12
    keys["Squash"].append((t, -0.4))
    for i in range(count):
        side = 1 if i % 2 == 0 else -1
        t1 = t + 0.06
        apex = t1 + air / 2
        keys["Squash"] += [(t1, 0.35), (apex, 0.05)]
        keys["PosY"] += ballistic(t1, air, height)
        keys["SkirtFlare"] += [(apex, 0.5), (t1 + air, 0.35)]
        keys["FootPoint"] += [(t1 + 0.06, 1.0), (t1 + air - 0.05, 0.8), (t1 + air, 0.0)]
        keys["AngleY"] += [(apex, 0.25), (t1 + air, -0.15)]
        keys["Hip"] += [(apex, 0.3 * side)]
        keys[f"Arm.{out}"] += [(apex, 0.4)]
        keys[f"Arm.{book}"] += [(apex, 0.2)]
        t = t1 + air
        keys["Squash"].append((t + 0.06, -0.45))
        t += 0.06
    end = t + 0.28
    for k in keys.values():
        k.append((end, 0))
    return Motion(name, end, keys, expression="happy")


def spin(out, book, name="spin"):
    d = 0.95
    curves = {
        "Spin": [(0, 0), (0.12, 0), (0.68, 1.0), (d, 1.0)],
        "PosY": [(0, 0), (0.12, 0)] + ballistic(0.14, 0.52, 0.22) + [(d, 0)],
        "Squash": [(0, 0), (0.1, -0.35), (0.16, 0.3), (0.42, 0.15), (0.64, 0.2), (0.72, -0.4), (d, 0)],
        "SkirtFlare": [(0, 0), (0.18, 0.6), (0.42, 1.0), (0.7, 0.8), (d, 0)],
        f"Arm.{out}": [(0, 0), (0.16, 0.5), (0.62, 0.5), (d, 0)],
        f"Arm.{book}": [(0, 0), (0.16, 0.3), (0.62, 0.3), (d, 0)],
        "AngleZ": [(0, 0), (0.18, 0.3), (0.62, 0.3), (d, 0)],
        "AngleY": [(0, 0), (0.18, 0.25), (0.62, 0.2), (d, 0)],
        "FootPoint": [(0, 0), (0.16, 1.0), (0.62, 1.0), (0.7, 0), (d, 0)],
    }
    return Motion(name, d, curves, expression="happy", events=[(0.25, "petals"), (0.5, "petals")])


def sidestep(out, book, dx, name="step"):
    d = 1.15
    s = math.copysign(1.0, dx)
    curves = {
        "PosX": [(0, 0), (0.1, 0), (0.98, dx), (d, dx)],
        "PosY": [(0, 0), (0.12, 0)] + ballistic(0.15, 0.34, 0.08) + ballistic(0.57, 0.34, 0.08) + [(d, 0)],
        "Squash": [(0, 0), (0.12, -0.25), (0.18, 0.15), (0.5, -0.22), (0.6, 0.15), (0.92, -0.22), (d, 0)],
        "Lean": [(0, 0), (0.15, 0.4 * s), (0.8, 0.25 * s), (d, 0)],
        "Hip": [(0, 0), (0.3, -0.3 * s), (0.72, 0.3 * s), (d, 0)],
        "SkirtFlare": [(0, 0), (0.3, 0.3), (0.9, 0.2), (d, 0)],
        "FootLift": [(0, 0), (0.22, 0.5), (0.48, 0), (0.64, 0.5), (0.9, 0), (d, 0)],
        "AngleX": [(0, 0), (0.15, 0.35 * s), (0.8, 0.35 * s), (d, 0)],
        f"Arm.{out}": [(0, 0), (0.3, 0.3), (0.72, -0.05), (d, 0)],
        f"Arm.{book}": [(0, 0), (0.3, -0.05), (0.72, 0.2), (d, 0)],
    }
    return Motion(name, d, curves, travel=dx)


def curtsy(out, book):
    d = 2.0
    hold = [(0, 0), (0.5, 1.0), (1.3, 1.0), (d, 0)]

    def scaled(k):
        return [(t, v * k) for t, v in hold]

    curves = {"Squash": scaled(-0.17), "PosY": scaled(-0.04), "AngleY": scaled(-0.6), "AngleZ": scaled(0.25),
              "SkirtFlare": scaled(0.5), "Hip": scaled(-0.2), f"Arm.{out}": scaled(0.3), f"Arm.{book}": scaled(0.1),
              **_both("Shoulder", scaled(-0.3))}
    return Motion("curtsy", d, curves, fade=0.2, expression="smile")


def tiptoe(out, book):
    d = 2.0
    curves = {
        "FootPoint": [(0, 0), (0.25, 1.0), (1.7, 1.0), (d, 0)],
        "FootLift": [(0, 0), (0.25, 0.3), (1.7, 0.3), (d, 0)],
        "PosY": [(0, 0), (0.2, 0.04)] + oscillate(0.25, 1.7, 0.5, 0.025, base=0.045) + [(d, 0)],
        "Squash": [(0, 0), (0.25, 0.1), (1.7, 0.08), (d, 0)],
        "Hip": [(0, 0)] + oscillate(0.2, 1.8, 1.0, 0.18) + [(d, 0)],
        f"Arm.{out}": [(0, 0), (0.3, 0.25), (1.6, 0.25), (d, 0)],
        f"Arm.{book}": [(0, 0), (0.3, 0.15), (1.6, 0.15), (d, 0)],
        "AngleY": [(0, 0), (0.3, 0.2), (1.6, 0.2), (d, 0)],
        **_both("Shoulder", [(0, 0), (0.3, 0.3), (1.6, 0.3), (d, 0)]),
    }
    return Motion("tiptoe", d, curves, expression="happy")


def foottap(out, book, beat=0.5):
    taps = [(0, 0)]
    for k in range(4):
        t = 0.3 + k * beat
        taps += [(t, 0), (t + 0.12, 0.45), (t + 0.26, 0)]
    d = 0.3 + 4 * beat + 0.3
    curves = {"FootLift": taps + [(d, 0)], "FootPoint": [(t, 0.6 * v) for t, v in taps] + [(d, 0)],
              "AngleY": oscillate(0, d, beat * 2, 0.08), "Hip": [(0, 0), (0.3, 0.25), (d - 0.3, 0.25), (d, 0)],
              "BodyAngleZ": [(0, 0), (0.3, -0.15), (d - 0.3, -0.15), (d, 0)],
              "AngleZ": oscillate(0, d, beat * 2, 0.12)}
    return Motion("foottap", d, curves)


def sway(out, book, period=1.6):
    d = 2 * period
    flare = [(0, 0)] + oscillate(0.4, d - 0.3, period / 2, 0.15, base=0.3) + [(d, 0)]
    bounce = [(0, 0)]
    for k in range(4):
        bounce += ballistic(0.05 + k * period / 2, period / 2 - 0.1, 0.035)
    curves = {
        "Hip": oscillate(0, d, period, 0.6),
        "BodyAngleZ": oscillate(0, d, period, 0.35, phase=math.pi),
        "AngleZ": oscillate(0, d, period, 0.35),
        f"Arm.{out}": _swing(d, period, 0.35, base=0.1),
        f"Arm.{book}": oscillate(0, d, period, 0.2, phase=math.pi),
        f"Shoulder.{out}": oscillate(0, d, period, 0.4),
        f"Shoulder.{book}": oscillate(0, d, period, 0.4, phase=math.pi),
        "PosY": bounce + [(d, 0)],
        "SkirtFlare": flare,
    }
    return Motion("sway", d, curves, fade=0.2, expression="smile", talk=True)


def run(out, book, step=0.32, steps=7):
    d = 0.1 + steps * step + 0.2
    pos, squash, foot = [(0, 0)], [(0, 0)], [(0, 0)]
    for k in range(steps):
        t = 0.1 + k * step
        pos += ballistic(t, step * 0.8, 0.06)
        squash += [(t, -0.25), (t + step * 0.4, 0.12)]
        if k % 2 == 0:
            foot += [(t + 0.04, 0), (t + 0.14, 0.6), (t + step - 0.02, 0)]
    for k in (pos, squash, foot):
        k.append((d, 0))
    curves = {
        "PosY": pos, "Squash": squash, "FootLift": foot,
        f"Arm.{out}": _swing(d, 2 * step, 0.4, base=0.15),
        f"Forearm.{out}": _swing(d, 2 * step, 0.3, base=-0.45, phase=math.pi / 2),
        f"Arm.{book}": _swing(d, 2 * step, 0.3, base=0.15, phase=math.pi),
        f"Shoulder.{out}": oscillate(0, d, 2 * step, 0.3),
        f"Shoulder.{book}": oscillate(0, d, 2 * step, 0.3, phase=math.pi),
        "Hip": oscillate(0, d, 2 * step, 0.2),
        "Lean": [(0, 0), (0.3, 0.1), (d - 0.3, 0.1), (d, 0)],
        "AngleY": [(0, 0), (0.3, 0.15), (d - 0.3, 0.15), (d, 0)],
        "SkirtFlare": [(0, 0), (0.3, 0.4), (d - 0.3, 0.4), (d, 0)],
    }
    return Motion("run", d, curves, fade=0.15, expression="determined")


def stretch(out, book):
    d = 2.8
    curves = {
        "Squash": [(0, 0), (0.9, 0.2), (1.9, 0.18), (d, 0)],
        "AngleY": [(0, 0), (0.9, 0.45), (1.9, 0.4), (d, 0)],
        "Lean": [(0, 0), (1.0, 0.2), (1.5, -0.2), (2.0, 0), (d, 0)],
        "BodyAngleZ": [(0, 0), (1.0, 0.3), (1.5, -0.3), (2.0, 0), (d, 0)],
        "FootPoint": [(0, 0), (0.9, 0.6), (1.9, 0.6), (d, 0)],
        "PosY": [(0, 0), (0.9, 0.03), (1.9, 0.03), (d, 0)],
        f"Arm.{out}": [(0, 0), (0.9, 0.35), (1.9, 0.35), (d, 0)],
        f"Arm.{book}": [(0, 0), (0.9, 0.2), (1.9, 0.2), (d, 0)],
        **_both("Shoulder", [(0, 0), (0.9, 0.9), (1.9, 0.85), (d, 0)]),
    }
    return Motion("stretch", d, curves, fade=0.25, expression="sleepy")


def bow(out, book):
    d = 1.8
    curves = {"AngleY": [(0, 0), (0.5, -0.85), (1.2, -0.85), (d, 0)],
              "Squash": [(0, 0), (0.5, -0.15), (1.2, -0.15), (d, 0)],
              **_both("Shoulder", [(0, 0), (0.5, -0.2), (1.2, -0.2), (d, 0)])}
    return Motion("bow", d, curves, fade=0.2, expression="smile")


def peek(out, book, orb):
    d = 2.4
    hold = [(0, 0), (0.45, 1.0), (1.9, 1.0), (d, 0)]

    def scaled(k):
        return [(t, v * k) for t, v in hold]

    curves = {"Lean": scaled(0.45 * orb), "AngleX": scaled(0.6 * orb), "EyeBallX": scaled(0.9 * orb),
              "Hip": scaled(-0.3 * orb), "AngleZ": scaled(0.25 * orb), "BodyAngleZ": scaled(0.2 * orb),
              "PosY": [(0, 0), (0.3, 0), (0.42, 0.04), (0.55, 0), (d, 0)]}
    return Motion("peek", d, curves, fade=0.2, expression="surprised")


def tailswish(out, book):
    d = 1.6
    curves = {"TailSwish": [(0, 0)] + oscillate(0.1, d - 0.15, 0.7, 0.9) + [(d, 0)],
              "Hip": [(0, 0)] + oscillate(0.1, d - 0.15, 0.7, 0.18, phase=math.pi) + [(d, 0)],
              "AngleZ": [(0, 0), (0.3, 0.15), (d - 0.3, 0.15), (d, 0)]}
    return Motion("tailswish", d, curves)


def wingflap(out, book):
    d = 1.5
    curves = {"WingFlap": [(0, 0), (0.2, -0.25), (0.42, 1.0), (0.62, -0.1), (0.84, 0.9), (1.06, -0.05), (1.3, 0.3),
                           (d, 0)],
              "PosY": [(0, 0), (0.42, 0.03), (0.62, 0.0), (0.84, 0.035), (1.06, 0.0), (d, 0)],
              "Squash": [(0, 0), (0.2, -0.15), (0.42, 0.12), (0.62, -0.05), (0.84, 0.1), (d, 0)],
              **_both("Shoulder", [(0, 0), (0.42, 0.3), (0.84, 0.25), (d, 0)])}
    return Motion("wingflap", d, curves, events=[(0.42, "petals")])


def eartwitch(out, book):
    d = 1.0

    def flick(t0):
        return [(0, 0), (t0, 0), (t0 + 0.06, 1.0), (t0 + 0.14, -0.35), (t0 + 0.24, 0.15), (t0 + 0.36, 0), (d, 0)]

    curves = {"EarTwitch.left": flick(0.08), "EarTwitch.right": flick(0.3),
              "AngleZ": [(0, 0), (0.2, 0.12), (0.6, -0.08), (d, 0)],
              "EyeBallX": [(0, 0), (0.2, 0.5), (0.6, -0.3), (d, 0)]}
    return Motion("eartwitch", d, curves)


def floatbob(out, book):
    """Drifting as if under water: a slow bob and sway, the arms floating up."""
    d = 3.4
    curves = {"PosY": [(0, 0)] + oscillate(0.2, d - 0.3, 1.6, 0.035, base=0.03) + [(d, 0)],
              "Lean": _swing(d, 1.7, 0.18),
              "AngleZ": _swing(d, 3.2, 0.3),
              "Hip": _swing(d, 1.7, 0.22, phase=math.pi),
              f"Arm.{out}": _swing(d, 1.6, 0.15, base=0.3, ease=0.5),
              f"Arm.{book}": _swing(d, 1.6, 0.1, base=0.2, phase=math.pi, ease=0.5),
              "SkirtFlare": [(0, 0), (0.5, 0.3), (d - 0.5, 0.3), (d, 0)],
              "TailSwish": _swing(d, 1.7, 0.5),
              "AngleY": [(0, 0), (0.6, 0.15), (d - 0.6, 0.15), (d, 0)]}
    return Motion("floatbob", d, curves, fade=0.25, expression="smile")


def pounce(out, book, orb):
    """A crouch, a wiggle, then a small hop toward the orb, paw first."""
    d = 1.25
    curves = {"Squash": [(0, 0), (0.3, -0.6), (0.5, -0.62), (0.56, 0.4), (0.76, 0.1), (0.86, -0.45), (1.0, 0.1), (d, 0)],
              "PosY": [(0, 0), (0.5, 0)] + ballistic(0.54, 0.32, 0.22) + [(d, 0)],
              "Lean": [(0, 0), (0.3, 0.3 * orb), (0.5, 0.35 * orb), (0.7, 0.1 * orb), (d, 0)],
              f"Arm.{out}": [(0, 0), (0.3, -0.1), (0.6, 0.55), (0.9, 0.2), (d, 0)],
              f"Forearm.{out}": [(0, 0), (0.3, 0.2), (0.6, -0.4), (d, 0)],
              f"Arm.{book}": [(0, 0), (0.6, 0.3), (d, 0)],
              "AngleY": [(0, 0), (0.3, -0.3), (0.6, 0.25), (0.9, -0.1), (d, 0)],
              "FootPoint": [(0, 0), (0.54, 0.2), (0.6, 1.0), (0.84, 0.8), (0.86, 0), (d, 0)],
              "SkirtFlare": [(0, 0), (0.7, 0.5), (0.9, 0.6), (d, 0)],
              "TailSwish": [(0, 0), (0.3, -0.6), (0.6, 0.9), (d, 0)]}
    return Motion("pounce", d, curves, expression="happy", events=[(0.56, "petals")])


def swagger(out, book):
    """Chin up, weight on one hip, a shrug of the free shoulder: smug."""
    d = 1.9
    curves = {"Hip": [(0, 0), (0.4, 0.6), (1.4, 0.6), (d, 0)],
              "BodyAngleZ": [(0, 0), (0.4, -0.35), (1.4, -0.3), (d, 0)],
              "Lean": [(0, 0), (0.4, -0.15), (1.4, -0.12), (d, 0)],
              "AngleZ": [(0, 0), (0.45, 0.35), (1.4, 0.3), (d, 0)],
              "AngleY": [(0, 0), (0.45, 0.2), (1.4, 0.2), (d, 0)],
              f"Shoulder.{out}": [(0, 0), (0.4, 0.5), (0.8, 0.2), (1.2, 0.5), (d, 0)],
              f"Arm.{out}": [(0, 0), (0.4, 0.2), (1.4, 0.2), (d, 0)],
              "EyeBallX": [(0, 0), (0.45, 0.6), (1.4, 0.6), (d, 0)]}
    return Motion("swagger", d, curves, expression="determined")


MOVES = {"hop": hop, "jump": jump, "hops": hops, "spin": spin, "curtsy": curtsy, "tiptoe": tiptoe, "foottap": foottap,
         "sway": sway, "run": run, "stretch": stretch, "bow": bow}
# Moves of the motion profiles (spec/CHARACTERS.md): the rig parameters each needs, any one of them for ears.
PROFILE_MOVES = {"tailswish": (tailswish, ("TailSwish",)), "wingflap": (wingflap, ("WingFlap",)),
                 "eartwitch": (eartwitch, ("EarTwitch.left", "EarTwitch.right")), "floatbob": (floatbob, ("PosY",)),
                 "swagger": (swagger, ("Hip",))}
