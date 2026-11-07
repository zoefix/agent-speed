"""Parameters, the bone channels they drive, and the physics chains that write the hair and skirt parameters.

Channels: rotate (degrees, clockwise on screen), x and y (canvas pixels), scaleX and scaleY (added to 1).
"""


ARM_RANGES = {"left": (18, 22), "right": (-34, -30), "single": (18, 22)}
VARIANT_ARM_RANGE = 60.0
LIFT_RANGE = 60.0
VARIANT_FOREARM_RANGE = 30.0


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
    for side in ("left", "right"):
        if has(f"eye.{side}"):
            drivers.append(drive(f"EyeOpen.{side}", f"eye.{side}", "scaleY", [(0, -0.9), (1, 0)]))
            drivers.append(linear("EyeBallX", f"iris.{side}", "x", 0.028 * fw))
            drivers.append(linear("EyeBallY", f"iris.{side}", "y", 0.018 * fw))

    physics = []
    for side, (upper, lower) in ARM_RANGES.items():
        if has(f"arm.{side}"):
            params += [param(f"Arm.{side}"), param(f"Forearm.{side}"), param(f"Lift.{side}")]
            drivers.append(linear(f"Arm.{side}", f"arm.{side}", "rotate", upper))
            drivers.append(linear(f"Lift.{side}", f"arm.{side}", "rotate", LIFT_RANGE))
            drivers.append(linear(f"Forearm.{side}", f"forearm.{side}", "rotate", lower))

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
    if has("skirt"):
        params.append(param("Skirt"))
        drivers.append(linear("Skirt", "skirt", "rotate", -5))
        physics.append(chain("skirt", lm["waist"], [lm["hem"] - lm["waist"][1]], ["Skirt"], [5], fw,
                             stiffness=55, damping=7.5, wind=0.4, head=0.0))
    return params, drivers, physics


def chain(name, anchor, lengths, outputs, degrees, fw, stiffness, damping, wind, head=1.0):
    return {
        "name": name,
        "anchor": [float(anchor[0]), float(anchor[1])],
        "segments": [float(max(8.0, length)) for length in lengths],
        "inputs": [
            {"param": "AngleZ", "x": 0.2 * fw * head},
            {"param": "AngleX", "x": -0.06 * fw * head},
            {"param": "BodyAngleZ", "x": 0.25 * fw},
            {"param": "BodySway", "x": 0.3 * fw},
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

        for part in v.parts:
            part_drivers.append({"param": control(part.name), "part": part.name, "keys": [[0, 0], [1, 1]]})
        for name in v.replaces:
            if name in part_names:
                part_drivers.append({"param": control(name), "part": name, "keys": [[0, 1], [1, 0]]})
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
                         f"Forearm.{s}": _unit(v.lead_fore_deg / ARM_RANGES[s][1])},
                "approach": {arm_p: _unit(v.approach_deg / VARIANT_ARM_RANGE),
                             fore_p: _unit(v.approach_fore_deg / VARIANT_FOREARM_RANGE)},
            })
        meta.append(entry)
    return params, drivers, part_drivers, meta
