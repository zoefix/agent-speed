"""One-shot gestures with the body joining in.

Arm gestures swap in a pose variant: the old arm turns to meet the new one, they cross-fade, the new arm settles into
its pose, holds with a flourish, and the swap runs backwards. The shoulder rises with the arm, the weight shifts to the
other leg and the head follows the hand. Combos play one variant per arm. Head and face gestures move the body too.
"""
import math

from .curves import Motion, merge, oscillate

# id -> (hold seconds, flourish, expression, rises onto the toes)
GESTURES = {
    "wave": (1.6, "wave", "smile", True),
    "peace": (1.3, "bob", "happy", True),
    "fist": (1.2, "pump", "determined", False),
    "idea": (1.4, "pop", "surprised", True),
    "chin": (2.4, "think", None, False),
    "hairtuck": (1.6, "slow", "smile", True),
    "present": (1.8, "offer", "talk", False),
    "hip": (1.8, "lean", "determined", False),
    "hug": (2.4, "sway", "smile", True),
    "raise": (1.4, "pump", "happy", False),
    "open": (3.4, "read", None, False),
}
# id -> (gesture of the object arm, gesture of the free arm, hold seconds, expression)
COMBOS = {
    "cheer": ("raise", "fist", 1.4, "happy"),
    "hooray": ("raise", "wave", 1.6, "happy"),
    "eureka": ("open", "idea", 1.8, "surprised"),
    "ponder": ("open", "chin", 2.4, None),
    "cuddle": ("hug", "peace", 1.6, "happy"),
    "proud": ("hug", "hip", 1.8, "determined"),
}
HEAD_GESTURES = ("nod", "shake", "tilt", "lookaround", "giggle", "doubletake")
FACE_GESTURES = ("yawn", "hum")


def timeline(hold):
    t1 = 0.22
    t2 = t1 + 0.1
    t3 = t2 + 0.32
    t4 = t3 + hold
    t5 = t4 + 0.24
    t6 = t5 + 0.1
    t7 = t6 + 0.36
    return t1, t2, t3, t4, t5, t6, t7


def arm_gesture(meta, hold, flourish, expression, tiptoe=False):
    var, arm, fore, side = meta["param"], meta["arm"], meta["forearm"], meta["side"]
    sign = 1.0 if side == "right" else -1.0
    t1, t2, t3, t4, t5, t6, t7 = timeline(hold)
    curves = {var: [(0, 0), (t1, 0), (t2, 1), (t5, 1), (t6, 0), (t7, 0)],
              f"Arm.{side}": [(0, 0), (t7, 0)], f"Forearm.{side}": [(0, 0), (t7, 0)]}
    for pid, lead in meta["lead"].items():
        dip = -0.12 * math.copysign(1.0, lead) if abs(lead) > 0.2 else 0.0
        curves[pid] = [(0, 0), (0.08, dip), (t1 - 0.04, lead), (t6 + 0.02, lead), (t7, 0)]
    for pid in (arm, fore):
        a = meta["approach"].get(pid, 0.0)
        curves[pid] = [(0, a), (t1, a), (t3, 0), (t4, 0), (t5 - 0.04, a), (t7, a)]

    def during(pid, keys):
        curves[pid] = curves[pid][:3] + keys + curves[pid][-3:]

    lift = 0.6 if meta.get("front") else 0.25
    body = {
        f"Shoulder.{side}": [(0, 0), (t1, 0.3 * lift), (t3, lift), (t4, 0.9 * lift), (t6, 0.3 * lift), (t7, 0)],
        "Hip": [(0, 0), (t3, -0.3 * sign), (t4, -0.3 * sign), (t7, 0)],
        "BodyAngleZ": [(0, 0), (t3, 0.2 * sign), (t4, 0.2 * sign), (t7, 0)],
    }
    if tiptoe:
        body.update({"FootPoint": [(0, 0), (t2, 0), (t3, 0.8), (t4, 0.8), (t5, 0), (t7, 0)],
                     "FootLift": [(0, 0), (t2, 0), (t3, 0.25), (t4, 0.25), (t5, 0), (t7, 0)],
                     "PosY": [(0, 0), (t2, 0), (t3, 0.025), (t4, 0.025), (t5, 0), (t7, 0)]})

    head = {}
    if flourish == "wave":
        during(fore, oscillate(t3, t4, 0.42, 0.45))
        head = {"AngleZ": [(0, 0), (t3, 0.25), (t4, 0.25), (t7, 0)], "AngleX": [(0, 0), (t3, -0.3), (t4, -0.3), (t7, 0)]}
    elif flourish == "bob":
        during(arm, oscillate(t3, t4, 0.5, 0.06))
        head = {"AngleZ": [(0, 0), (t3, 0.45), (t4, 0.45), (t7, 0)]}
    elif flourish == "pump":
        during(arm, oscillate(t3, t4, 0.34, 0.12))
        head = {"AngleY": [(0, 0), (t3, 0.4), (t4, 0.4), (t7, 0)],
                "BodyAngleZ": [(0, 0), (t3, -0.4 * sign), (t4, -0.4 * sign), (t7, 0)]}
        body["PosY"] = [(0, 0), (t2, 0)] + oscillate(t3, t4, 0.68, 0.03, base=0.03) + [(t5, 0), (t7, 0)]
    elif flourish == "pop":
        during(fore, [(t3 + 0.12, -0.2), (t3 + 0.3, 0)])
        head = {"AngleY": [(0, 0), (t3, 0.5), (t4, 0.45), (t7, 0)],
                "EyeBallY": [(0, 0), (t3, -0.7), (t4, -0.7), (t7, 0)]}
    elif flourish == "think":
        head = {"AngleZ": [(0, 0), (t3, 0.35), (t4, 0.4), (t7, 0)], "AngleY": [(0, 0), (t3, 0.25), (t4, 0.2), (t7, 0)],
                "EyeBallY": [(0, 0), (t3, -0.8), (t4, -0.8), (t7, 0)], "EyeBallX": [(0, 0), (t3, 0.5), (t4, 0.6), (t7, 0)]}
    elif flourish == "slow":
        during(fore, [(t3 + hold * 0.5, 0.15)])
        head = {"AngleZ": [(0, 0), (t3, 0.4), (t4, 0.4), (t7, 0)], "EyeBallX": [(0, 0), (t3, 0.4), (t4, 0.4), (t7, 0)]}
    elif flourish == "offer":
        during(arm, [(t3 + hold * 0.5, -0.05)])
        head = {"AngleX": [(0, 0), (t3, 0.45), (t4, 0.45), (t7, 0)], "EyeBallX": [(0, 0), (t3, 0.7), (t4, 0.7), (t7, 0)]}
        body["Lean"] = [(0, 0), (t3, 0.25 * sign), (t4, 0.25 * sign), (t7, 0)]
    elif flourish == "lean":
        head = {"BodyAngleZ": [(0, 0), (t3, 0.55 * sign), (t4, 0.55 * sign), (t7, 0)],
                "AngleZ": [(0, 0), (t3, -0.3 * sign), (t4, -0.3 * sign), (t7, 0)]}
        body["Hip"] = [(0, 0), (t3, 0.55 * sign), (t4, 0.55 * sign), (t7, 0)]
    elif flourish == "sway":
        head = {"AngleZ": [(0, 0), (t3, -0.3), (t3 + hold / 2, 0.2), (t4, -0.3), (t7, 0)],
                "BodyAngleZ": oscillate(0, t7, 1.6, 0.25)}
        body["Hip"] = oscillate(0, t7, 1.6, 0.3, phase=math.pi)
    elif flourish == "read":
        head = {"AngleY": [(0, 0), (t3, -0.6), (t4, -0.6), (t7, 0)],
                "EyeBallY": [(0, 0), (t3, 0.6), (t4, 0.6), (t7, 0)],
                "EyeBallX": [(0, 0), (t3, -0.6)] + oscillate(t3, t4, 1.1, 0.5, base=0.0) + [(t7, 0)]}
    if flourish not in ("wave", "pop", "slow"):
        during(fore, oscillate(t3, t4, 1.3, 0.04))
    curves.update(body)
    curves.update(head)
    return Motion(meta["id"], t7, curves, expression=expression)


def combo(name, book_meta, free_meta, hold, expression):
    a = arm_gesture(book_meta, hold, GESTURES[book_meta["id"]][1], None)
    b = arm_gesture(free_meta, hold, GESTURES[free_meta["id"]][1], expression)
    return merge(name, [a, b], delays=[0.0, 0.1], expression=expression)


def head_gesture(name):
    if name == "nod":
        return Motion("nod", 1.1, {"AngleY": [(0, 0), (0.18, 0.4), (0.38, -0.15), (0.58, 0.35), (0.8, -0.05), (1.1, 0)],
                                   "Squash": [(0, 0), (0.18, -0.12), (0.38, 0.04), (0.58, -0.1), (0.8, 0), (1.1, 0)],
                                   "EyeOpen.left": [(0, 1), (0.2, 0.7), (0.6, 0.7), (1.1, 1)],
                                   "EyeOpen.right": [(0, 1), (0.2, 0.7), (0.6, 0.7), (1.1, 1)]},
                      expression="smile")
    if name == "shake":
        return Motion("shake", 1.3, {"AngleX": [(0, 0)] + oscillate(0.1, 1.1, 0.42, 0.4) + [(1.3, 0)],
                                     "BodyAngleZ": [(0, 0)] + oscillate(0.15, 1.15, 0.42, 0.12) + [(1.3, 0)],
                                     "AngleY": [(0, 0), (0.2, -0.15), (1.1, -0.15), (1.3, 0)]})
    if name == "tilt":
        return Motion("tilt", 2.2, {"AngleZ": [(0, 0), (0.35, 0.7), (1.8, 0.65), (2.2, 0)],
                                    "AngleX": [(0, 0), (0.35, 0.25), (1.8, 0.25), (2.2, 0)],
                                    "EyeBallX": [(0, 0), (0.3, 0.5), (1.8, 0.5), (2.2, 0)],
                                    "Hip": [(0, 0), (0.45, 0.35), (1.8, 0.35), (2.2, 0)],
                                    "BodyAngleZ": [(0, 0), (0.45, -0.2), (1.8, -0.2), (2.2, 0)]},
                      fade=0.2, expression="smile")
    if name == "lookaround":
        return Motion("lookaround", 2.8, {
            "EyeBallX": [(0, 0), (0.2, -0.9), (0.9, -0.9), (1.1, 0.9), (2.2, 0.9), (2.8, 0)],
            "AngleX": [(0, 0), (0.45, -0.55), (1.0, -0.55), (1.6, 0.55), (2.3, 0.5), (2.8, 0)],
            "AngleY": [(0, 0), (0.45, 0.1), (1.3, -0.05), (2.3, 0.1), (2.8, 0)],
            "Hip": [(0, 0), (0.5, -0.25), (1.0, -0.25), (1.7, 0.25), (2.3, 0.2), (2.8, 0)],
            "Lean": [(0, 0), (0.5, -0.15), (1.0, -0.15), (1.7, 0.15), (2.3, 0.1), (2.8, 0)],
        }, fade=0.2)
    if name == "giggle":
        return Motion("giggle", 1.6, {"BodyAngleZ": [(0, 0)] + oscillate(0.1, 1.4, 0.17, 0.12) + [(1.6, 0)],
                                      "Shoulder.left": [(0, 0)] + oscillate(0.1, 1.4, 0.34, 0.25, base=0.3) + [(1.6, 0)],
                                      "Shoulder.right": [(0, 0)] + oscillate(0.1, 1.4, 0.34, 0.25, base=0.3) + [(1.6, 0)],
                                      "Squash": [(0, 0)] + oscillate(0.1, 1.4, 0.34, 0.08, base=-0.05) + [(1.6, 0)],
                                      "AngleY": [(0, 0), (0.2, -0.25), (1.4, -0.2), (1.6, 0)],
                                      "AngleZ": [(0, 0), (0.2, 0.3), (1.4, 0.3), (1.6, 0)],
                                      "Breath": [(0, 0), (0.3, 1), (1.6, 0.3)]},
                      expression="happy")
    return Motion("doubletake", 1.5, {
        "AngleX": [(0, 0), (0.15, 0.35), (0.35, 0.3), (0.5, -0.1), (0.65, 0.6), (1.2, 0.5), (1.5, 0)],
        "AngleY": [(0, 0), (0.6, 0.35), (1.2, 0.3), (1.5, 0)],
        "EyeBallX": [(0, 0), (0.6, 0.8), (1.2, 0.8), (1.5, 0)],
        "PosY": [(0, 0), (0.55, 0), (0.66, 0.07), (0.8, 0), (1.5, 0)],
        "Squash": [(0, 0), (0.5, -0.25), (0.62, 0.3), (0.82, -0.15), (1.0, 0), (1.5, 0)],
        "Shoulder.left": [(0, 0), (0.62, 0.6), (1.2, 0.3), (1.5, 0)],
        "Shoulder.right": [(0, 0), (0.62, 0.6), (1.2, 0.3), (1.5, 0)],
    }, fade=0.1, expression="surprised")


def face_gesture(name, duration=2.4):
    if name == "yawn":
        return Motion("yawn", duration, {
            "AngleY": [(0, 0), (0.5, 0.45), (1.6, 0.4), (duration, 0)],
            "AngleZ": [(0, 0), (0.6, -0.35), (duration, 0)],
            "Breath": [(0, 0), (0.7, 1.0), (1.8, 0.2), (duration, 0)],
            "Squash": [(0, 0), (0.7, 0.15), (1.5, 0.13), (duration, 0)],
            "Shoulder.left": [(0, 0), (0.7, 0.9), (1.6, 0.8), (duration, 0)],
            "Shoulder.right": [(0, 0), (0.7, 0.9), (1.6, 0.8), (duration, 0)],
            "FootPoint": [(0, 0), (0.7, 0.5), (1.5, 0.5), (duration, 0)],
            "Lean": [(0, 0), (0.8, 0.1), (1.6, -0.08), (duration, 0)],
        }, fade=0.25, expression="sleepy")
    d = duration + 1.0
    return Motion("hum", d, {
        "AngleZ": oscillate(0, d, 1.2, 0.3),
        "AngleX": [(0, 0), (0.4, -0.25), (d, 0)],
        "Hip": oscillate(0, d, 1.2, 0.3, phase=math.pi),
        "BodyAngleZ": oscillate(0, d, 1.2, 0.15),
    }, fade=0.25, expression="smile", talk=True)
