"""Parameters, the bone channels they drive, and the physics chains that write the hair and skirt parameters.

Whole-body parameters: PosX and PosY move the character (+1 is right and up), Squash stretches (+) or squashes (-)
it over the feet, Lean tilts it over the feet, Spin (0..1) is one full paper-doll turn, Hip shifts the weight.

Channels: rotate (degrees, clockwise on screen), x and y (canvas pixels), scaleX and scaleY (added to 1).
"""


import math

import numpy as np

from .hands import CURL, FIST, SHORTEN, SPREAD

ARM_RANGES = {"left": (18, 22), "right": (-34, -30), "single": (18, 22)}
VARIANT_ARM_RANGE = 60.0
LIFT_RANGE = 60.0
VARIANT_FOREARM_RANGE = 90.0
BEND_RANGE = 90.0
# How far the irises travel at EyeBall ±1, in face widths: a small glance, never sliding off to show the white.
IRIS_TRAVEL = (0.017, 0.009)
POS_X, POS_Y, HIP_X, HIP_COUNTER = 3.0, 2.0, 0.17, 0.08


def param(pid, lo=-1.0, hi=1.0, default=0.0):
    return {"id": pid, "min": lo, "max": hi, "default": default}


def drive(pid, bone, channel, keys):
    return {"param": pid, "bone": bone, "channel": channel, "keys": [list(k) for k in keys]}


def linear(pid, bone, channel, amount):
    return drive(pid, bone, channel, [(-1, -amount), (1, amount)])


def build(lm, bone_names):
    fw = lm["faceWidth"]
    has = set(bone_names).__contains__
    params = [
        param("AngleX"), param("AngleY"), param("AngleZ"),
        param("BodyAngleZ"), param("BodySway"), param("Breath", 0, 1),
        param("EyeOpen.left", 0, 1, 1), param("EyeOpen.right", 0, 1, 1),
        param("EyeBallX"), param("EyeBallY"),
        param("Wind", 0, 1),
    ]
    drivers = [
        linear("AngleZ", "head", "rotate", 9),
        linear("AngleX", "face", "x", 0.02 * fw),
        drive("AngleX", "face", "scaleX", [(-1, -0.02), (0, 0), (1, -0.02)]),
        linear("AngleX", "features", "x", 0.045 * fw),
        linear("AngleX", "bangs", "x", 0.05 * fw),
        linear("AngleX", "side", "x", -0.012 * fw),
        linear("AngleX", "hairTop", "x", -0.012 * fw),
        linear("AngleY", "face", "y", 0.015 * fw),
        linear("AngleY", "features", "y", 0.032 * fw),
        linear("AngleY", "bangs", "y", 0.036 * fw),
        linear("AngleY", "side", "y", -0.012 * fw),
        drive("AngleY", "features", "scaleY", [(-1, -0.03), (0, 0), (1, 0.0)]),
        linear("BodyAngleZ", "chest", "rotate", 3.5),
        linear("BodySway", "lower", "rotate", 1.2),
        drive("Breath", "chest", "scaleY", [(0, 0), (1, 0.012)]),
        drive("Breath", "chest", "y", [(0, 0), (1, -0.8)]),
    ]
    if has("turn"):
        params += [param("PosX"), param("PosY"), param("Squash"), param("Lean"), param("Spin", 0, 1), param("Hip"),
                   param("SkirtFlare", 0, 1)]
        drivers += [
            linear("PosX", "root", "x", POS_X * fw),
            linear("PosY", "root", "y", -POS_Y * fw),
            linear("Squash", "root", "scaleY", 0.1),
            linear("Squash", "root", "scaleX", -0.06),
            linear("Lean", "root", "rotate", 6),
            drive("Spin", "turn", "scaleX", [(k / 16, math.cos(2 * math.pi * k / 16) - 1) for k in range(17)]),
            linear("Hip", "lower", "x", HIP_X * fw),
            linear("Hip", "chest", "x", -HIP_COUNTER * fw),
            linear("Hip", "chest", "rotate", -0.8),
            drive("SkirtFlare", "skirtHem", "scaleX", [(0, 0), (1, 0.14)]),
            drive("SkirtFlare", "skirtHem", "scaleY", [(0, 0), (1, -0.03)]),
        ]
    if has("foot"):
        params += [param("FootLift", 0, 1), param("FootPoint", 0, 1)]
        drivers += [drive("FootLift", "foot", "y", [(0, 0), (1, -0.35 * fw)]),
                    drive("FootPoint", "foot", "scaleY", [(0, 0), (1, 0.15)])]
    for side in ("left", "right"):
        if has(f"shoulder.{side}"):
            params.append(param(f"Shoulder.{side}"))
            drivers.append(linear(f"Shoulder.{side}", f"shoulder.{side}", "y", -0.07 * fw))
        if has(f"eye.{side}"):
            drivers.append(drive(f"EyeOpen.{side}", f"eye.{side}", "scaleY", [(0, -0.9), (1, 0)]))
            drivers.append(linear("EyeBallX", f"iris.{side}", "x", IRIS_TRAVEL[0] * fw))
            drivers.append(linear("EyeBallY", f"iris.{side}", "y", IRIS_TRAVEL[1] * fw))

    physics = []
    for side, (upper, lower) in ARM_RANGES.items():
        if has(f"arm.{side}"):
            params += [param(f"Arm.{side}"), param(f"Forearm.{side}"), param(f"Lift.{side}"), param(f"Bend.{side}")]
            drivers.append(linear(f"Arm.{side}", f"arm.{side}", "rotate", upper))
            drivers.append(linear(f"Lift.{side}", f"arm.{side}", "rotate", LIFT_RANGE))
            drivers.append(linear(f"Forearm.{side}", f"forearm.{side}", "rotate", lower))
            drivers.append(linear(f"Bend.{side}", f"forearm.{side}", "rotate", BEND_RANGE))

    for side, hand in lm.get("hands", {}).items():
        if has(f"hand.{side}"):
            fist = hand["open"] < FIST
            params += [param(f"Wrist.{side}"), param(f"Spread.{side}", -0.1 if fist else -0.5, 0.1 if fist else 1.0)]
            params += [param(f"Finger.{side}.{i}", -0.08 if fist else -0.4, 0.12 if fist else 1.0) for i in range(5)]
            drivers += hand_drivers(side, hand)

    if has("hairBack1"):
        bh = lm["backHair"]
        params += [param("HairBack1"), param("HairBack2")]
        drivers += [linear("HairBack1", "hairBack1", "rotate", -14), linear("HairBack2", "hairBack2", "rotate", -12)]
        physics.append(chain("backHair", bh["root"], [bh["mid"][1] - bh["root"][1], bh["end"] - bh["mid"][1]],
                             ["HairBack1", "HairBack2"], [14, 12], fw, stiffness=30, damping=6.0, wind=0.95))
    if has("lock1"):
        lk = lm["lock"]
        params += [param("HairLock1"), param("HairLock2")]
        drivers += [linear("HairLock1", "lock1", "rotate", -20), linear("HairLock2", "lock2", "rotate", -24)]
        physics.append(chain("lock", lk["root"], [lk["mid"][1] - lk["root"][1], lk["end"] - lk["mid"][1]],
                             ["HairLock1", "HairLock2"], [20, 24], fw, stiffness=20, damping=5.0, wind=1.25))
    if has("skirtHem"):
        params += [param("Skirt"), param("SkirtHem")]
        drivers += [linear("Skirt", "skirt", "rotate", -5), linear("SkirtHem", "skirtHem", "rotate", -7)]
        physics.append(chain("skirt", lm["waist"], [lm["skirtMid"] - lm["waist"][1], lm["hem"] - lm["skirtMid"]],
                             ["Skirt", "SkirtHem"], [5, 7], fw, stiffness=45, damping=6.5, wind=0.4, head=0.0,
                             hip=HIP_X))
    elif has("skirt"):
        params.append(param("Skirt"))
        drivers.append(linear("Skirt", "skirt", "rotate", -5))
        physics.append(chain("skirt", lm["waist"], [lm["hem"] - lm["waist"][1]], ["Skirt"], [5], fw,
                             stiffness=55, damping=7.5, wind=0.4, head=0.0, hip=HIP_X))
    appendage_controls(lm, has, fw, params, drivers, physics)
    return params, drivers, physics


# Tail: the swish turns the root and its middle; the chain's follow-through bends each segment (degrees per unit).
TAIL_SWISH = (16.0, 10.0)
TAIL_BEND = (8.0, 12.0, 16.0)
WING_FLAP, WING_SPREAD = 14.0, 0.08
EAR_TWITCH = 12.0


def appendage_controls(lm, has, fw, params, drivers, physics):
    """Parameters, drivers and physics of tails, wings and animal ears (spec/CHARACTERS.md, Appendages)."""
    if has("tail1") and "tail" in lm:
        t = lm["tail"]
        params += [param("TailSwish"), param("TailBend1"), param("TailBend2"), param("TailBend3")]
        drivers += [linear("TailSwish", "tail1", "rotate", TAIL_SWISH[0]),
                    linear("TailSwish", "tail2", "rotate", TAIL_SWISH[1])]
        drivers += [linear(f"TailBend{i + 1}", bone, "rotate", -d) for i, (bone, d) in
                    enumerate(zip(("tail1", "tail2", "tail3"), TAIL_BEND))]
        lengths = [t["length"] / 3] * 3
        spec = chain("tail", t["root"], lengths, ["TailBend1", "TailBend2", "TailBend3"], list(TAIL_BEND), fw,
                     stiffness=28, damping=5.0, wind=0.7, head=0.0, hip=HIP_X)
        spec["inputs"].append({"param": "TailSwish", "x": 0.5 * fw})
        physics.append(spec)
    wings = [s for s in ("left", "right") if has(f"wing.{s}")]
    if wings:
        params.append(param("WingFlap"))
        for side in wings:
            sign = 1.0 if side == "left" else -1.0
            drivers += [linear("WingFlap", f"wing.{side}", "rotate", sign * WING_FLAP),
                        drive("WingFlap", f"wing.{side}", "scaleX", [(-1, -WING_SPREAD), (0, 0), (1, WING_SPREAD)])]
    for side in ("left", "right"):
        if has(f"ear.{side}"):
            sign = -1.0 if side == "left" else 1.0
            params.append(param(f"EarTwitch.{side}"))
            drivers += [linear(f"EarTwitch.{side}", f"ear.{side}", "rotate", sign * EAR_TWITCH),
                        drive(f"EarTwitch.{side}", f"ear.{side}", "scaleY", [(-1, 0.04), (0, 0), (1, -0.08)])]


def hand_drivers(side, hand):
    """Wrist turn, finger curl (rotation toward the palm plus the outer segments sliding toward the knuckles) and
    spread (each finger's base turning away from the middle finger)."""
    out = [linear(f"Wrist.{side}", f"hand.{side}", "rotate", 16)]
    for i, f in enumerate(hand["fingers"]):
        pid, sign = f"Finger.{side}.{i}", hand["curl"][i]
        for j in range(3):
            bone = f"finger.{side}.{i}.{j}"
            out.append(drive(pid, bone, "rotate", [(-0.4, -0.4 * sign * CURL[j]), (0, 0), (1, sign * CURL[j])]))
            if SHORTEN[j]:
                dx, dy = (np.asarray(f[j], float) - np.asarray(f[j - 1], float)) * -SHORTEN[j]
                out.append(drive(pid, bone, "x", [(-0.4, 0), (0, 0), (1, float(dx))]))
                out.append(drive(pid, bone, "y", [(-0.4, 0), (0, 0), (1, float(dy))]))
        if SPREAD[i]:
            out.append(linear(f"Spread.{side}", f"finger.{side}.{i}.0", "rotate", hand["spread"][i] * SPREAD[i]))
    return out


def chain(name, anchor, lengths, outputs, degrees, fw, stiffness, damping, wind, head=1.0, hip=HIP_X - HIP_COUNTER):
    return {
        "name": name,
        "anchor": [float(anchor[0]), float(anchor[1])],
        "segments": [float(max(8.0, length)) for length in lengths],
        "inputs": [
            {"param": "AngleZ", "x": 0.2 * fw * head},
            {"param": "AngleX", "x": -0.06 * fw * head},
            {"param": "BodyAngleZ", "x": 0.25 * fw},
            {"param": "BodySway", "x": 0.3 * fw},
            {"param": "PosX", "x": POS_X * fw},
            {"param": "PosY", "y": -POS_Y * fw},
            {"param": "Hip", "x": hip * fw},
            {"param": "Lean", "x": 0.5 * fw},
        ],
        "outputs": [{"param": p, "segment": i, "degrees": d} for i, (p, d) in enumerate(zip(outputs, degrees))],
        "stiffness": stiffness,
        "damping": damping,
        "wind": wind,
    }


def _unit(x):
    return round(max(-1.0, min(1.0, float(x))), 3)


def variant_controls(variants, part_names):
    """Parameters, bone drivers and part opacity drivers for pose and expression variants."""
    params, drivers, part_drivers, meta = [], [], [], []
    for v in variants:
        p = f"Var.{v.id}"
        mouth = f"Mouth.{v.id}"
        params.append(param(p, 0, 1, 0))
        if v.kind == "face":
            params.append(param(mouth, 0, 1, 0))

        def control(name):
            return mouth if v.kind == "face" and name.split("@")[0].split(".")[0] == "mouth" else p

        # The new arm is mostly in before the old one fades, so the swap barely turns see-through.
        fade_in, fade_out = [[0, 0], [1, 1]], [[0, 1], [1, 0]]
        if v.kind == "arm":
            fade_in, fade_out = [[0, 0], [0.6, 1]], [[0.4, 1], [1, 0]]
        for part in v.parts:
            part_drivers.append({"param": control(part.name), "part": part.name, "keys": fade_in})
        for name in v.replaces:
            if name in part_names:
                part_drivers.append({"param": control(name), "part": name, "keys": fade_out})
        entry = {"id": v.id, "kind": v.kind, "param": p}
        if v.kind == "face":
            entry.update({"mouth": mouth, "eyesClosed": not any(part.tag == "irides" for part in v.parts)})
        if v.kind == "arm":
            s = v.side
            arm_p, fore_p = f"Arm.{s}@{v.id}", f"Forearm.{s}@{v.id}"
            params += [param(arm_p), param(fore_p)]
            drivers += [linear(arm_p, f"arm.{s}@{v.id}", "rotate", VARIANT_ARM_RANGE),
                        linear(fore_p, f"forearm.{s}@{v.id}", "rotate", VARIANT_FOREARM_RANGE)]
            entry.update({
                "side": s, "front": bool(v.front), "arm": arm_p, "forearm": fore_p,
                "lead": {f"Lift.{s}": _unit(v.lead_deg / LIFT_RANGE),
                         f"Bend.{s}": _unit(v.lead_fore_deg / BEND_RANGE)},
                "approach": {arm_p: _unit(v.approach_deg / VARIANT_ARM_RANGE),
                             fore_p: _unit(v.approach_fore_deg / VARIANT_FOREARM_RANGE)},
            })
        meta.append(entry)
    return params, drivers, part_drivers, meta
