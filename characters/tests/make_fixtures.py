"""Writes the shared fixtures every native character player must reproduce (spec/CHARACTERS.md, Conformance).

  python3 -m characters.tests.make_fixtures [--check]

Each file holds inputs and the reference player's outputs. --check regenerates in memory and fails if a file differs,
so the fixtures never drift from the reference.
"""
import argparse
import json
import math
import sys
from pathlib import Path

import numpy as np

from characters.reference.hud import Odometer
from characters.reference.motions import Director
from characters.reference.motions.curves import Curve
from characters.reference.motions.springs import SecondOrder
from characters.reference.orbits import Orbit, eccentric_anomaly
from characters.reference.physics import Physics
from characters.reference.rig import Rig
from characters.reference.rng import Rng
from characters.reference.stage import Switch

OUT = Path(__file__).resolve().parents[2] / "spec" / "fixtures" / "characters"


def r(x, nd=9):
    if isinstance(x, dict):
        return {k: r(v, nd) for k, v in x.items()}
    if isinstance(x, (list, tuple, np.ndarray)):
        return [r(v, nd) for v in x]
    return round(float(x), nd)


def rng_cases():
    cases = []
    for seed in (0, 7, 123456789, 2 ** 63 + 5):
        g = Rng(seed)
        u64 = [str(g.next_u64()) for _ in range(5)]
        g = Rng(seed)
        floats = [g.random() for _ in range(5)]
        g = Rng(seed)
        items = list(range(10))
        g.shuffle(items)
        g2 = Rng(seed)
        cases.append({"seed": str(seed), "u64": u64, "random": floats, "uniform_2_5": Rng(seed).uniform(2, 5),
                      "shuffle10": items, "sample_abcde_3": g2.sample("abcde", 3)})
    return {"about": "SplitMix64; u64 values are decimal strings", "cases": cases}


def curve_cases():
    defs = [
        {"keys": [[0, 0], [1, 1], [2, 1], [3, 0.2], [4, 0.6]], "periodic": False},
        {"keys": [[0, 0], [0.5, 1], [1.2, -0.4], [2, 0]], "periodic": True},
        {"keys": [[0, 0.3], [0.2, 0.3], [0.6, 0.9], [0.6, 0.1], [1, 0]], "periodic": False},
        {"keys": [[0, 1]], "periodic": False},
    ]
    out = []
    for d in defs:
        c = Curve(d["keys"], periodic=d["periodic"])
        xs = [x / 20 for x in range(-4, 90)]
        out.append({**d, "x": xs, "y": r([c(x) for x in xs])})
    return {"cases": out}


def spring_cases():
    out = []
    for f, z, resp in ((2.6, 0.55, 0.0), (5.0, 0.55, 0.0), (1.8, 0.65, 0.0), (6.0, 0.85, 0.0), (2.0, 0.5, 1.5)):
        for dt in (1 / 60, 1 / 144, 1 / 20):
            s = SecondOrder(f, z, resp, 0.0)
            inputs = [0.0] * 3 + [1.0] * 40 + [-0.5] * 40 + [0.2] * 20
            outs = [s.update(dt, x) for x in inputs]
            out.append({"f": f, "z": z, "r": resp, "dt": dt, "x0": 0.0, "inputs": inputs, "outputs": r(outs)})
    return {"cases": out}


def orbit_cases():
    out = []
    for args in ((0.4 * 972, 0.2, 52.0, 0.35, 0.0, -0.06 * 972, 0.16, 0.012, 0.4),
                 (100.0, 0.4, 10.0, 1.0, 0.7, 5.0, 0.0, 0.0, 2.0)):
        o = Orbit(*args)
        ts = [t * 0.37 for t in range(120)]
        pos = [o.at(t, 500.0, 400.0) for t in ts]
        out.append({"a": args[0], "e": args[1], "period": args[2], "phase": args[3], "omega": args[4], "height": args[5],
                    "tilt": args[6], "precess": args[7], "seed": args[8], "center": [500.0, 400.0], "t": ts,
                    "xyDepth": r(pos), "spin": r([o.spin(t) for t in ts])})
    kepler = [{"mean": m, "e": e, "E": r(eccentric_anomaly(m, e), 12)}
              for m in (0.0, 0.3, 1.5, 3.1, 4.4, 6.2) for e in (0.0, 0.2, 0.6, 0.9)]
    return {"orbits": out, "kepler": kepler}


def tiny_rig():
    square = [0, 0, 10, 0, 10, 10, 0, 10]
    return {
        "format": "agent-speed-character", "version": 1, "name": "tiny", "canvas": [100, 100],
        "atlas": {"file": "atlas.png", "size": [16, 16]}, "landmarks": {},
        "params": [{"id": "Turn", "min": -1, "max": 1, "default": 0}, {"id": "Lift", "min": 0, "max": 1, "default": 0},
                   {"id": "Grow", "min": -1, "max": 1, "default": 0}, {"id": "Fade", "min": 0, "max": 1, "default": 0}],
        "bones": [{"name": "root", "parent": None, "pivot": [50, 90]},
                  {"name": "arm", "parent": "root", "pivot": [20, 30]},
                  {"name": "hand", "parent": "arm", "pivot": [30, 30]}],
        "drivers": [{"param": "Turn", "bone": "arm", "channel": "rotate", "keys": [[-1, -90], [0, 0], [1, 45]]},
                    {"param": "Lift", "bone": "hand", "channel": "y", "keys": [[0, 0], [1, -5]]},
                    {"param": "Grow", "bone": "root", "channel": "scaleX", "keys": [[-1, -0.5], [1, 0.25]]},
                    {"param": "Grow", "bone": "root", "channel": "scaleY", "keys": [[-1, 0.1], [1, -0.1]]},
                    {"param": "Turn", "bone": "root", "channel": "x", "keys": [[-1, 3], [1, -3]]}],
        "physics": [], "partDrivers": [{"param": "Fade", "part": "b", "keys": [[0, 1], [1, 0]]}], "variants": [],
        "parts": [
            {"name": "a", "tag": "topwear", "order": 0, "vertices": [20, 20, 30, 20, 30, 30, 20, 30], "uv": square,
             "triangles": [0, 1, 2, 0, 2, 3], "influences": 2, "bones": [1, 2] * 4,
             "weights": [1, 0, 0.5, 0.5, 0, 1, 0.25, 0.75]},
            {"name": "b", "tag": "face", "order": 1, "vertices": [40, 40, 60, 40, 60, 60], "uv": [0, 0, 10, 0, 10, 10],
             "triangles": [0, 1, 2], "influences": 1, "bones": [0, 0, 0], "weights": [1, 1, 1]},
        ],
    }


def rig_cases():
    doc = tiny_rig()
    rig = Rig(doc)
    poses = [{}, {"Turn": 1}, {"Turn": -0.5, "Lift": 1}, {"Grow": 0.7, "Turn": 0.3}, {"Turn": 5, "Fade": 0.4},
             {"Grow": -1, "Lift": 0.5, "Fade": 1}]
    out = []
    for values in poses:
        world = rig.bone_matrices(values)
        verts = rig.pose(values)
        out.append({"values": values, "world": r([m[:2].ravel().tolist() for m in world]),
                    "vertices": r([v.ravel().tolist() for v in verts]), "opacity": r(rig.opacities(values))})
    return {"about": "world: per bone [a, b, tx, c, d, ty] of the 2x3 world transform, x' = a x + b y + tx",
            "rig": doc, "cases": out}


def physics_cases():
    spec = {"name": "hair", "anchor": [10, 20], "segments": [100, 80],
            "inputs": [{"param": "Sway", "x": 50}, {"param": "Lift", "y": -30}],
            "outputs": [{"param": "H1", "segment": 0, "degrees": 10}, {"param": "H2", "segment": 1, "degrees": 12}],
            "stiffness": 30, "damping": 6, "wind": 0.9}
    out = []
    for wind_sign in (1.0, -1.0):
        p = Physics([spec], wind_sign=wind_sign)
        p.reset({})
        frames = []
        for n in range(240):
            t = n / 60
            values = {"Sway": math.sin(t * 2.1), "Lift": 0.5 + 0.5 * math.sin(t * 0.7), "Wind": min(1.0, t / 3)}
            o = p.step(1 / 60, values)
            frames.append({"in": r(values), "out": r(o)})
        out.append({"windSign": wind_sign, "dt": 1 / 60, "frames": frames})
    return {"spec": spec, "cases": out}


def odometer_cases():
    odo = Odometer()
    targets = [min(420.0, 3.0 * (n / 60) ** 2) for n in range(600)]
    texts = []
    for t in targets:
        text = odo.update(1 / 60, t)
        texts.append({"text": text, "digits": [[d["cur"], d["prev"], r(d["t"], 6)] for d in odo.digits]})
    return {"dt": 1 / 60, "targets": r(targets), "frames": texts}


def switch_cases():
    s = Switch()
    s.request("next")
    frames = []
    for n in range(50):
        values = {}
        swap = s.update(1 / 60, values)
        frames.append({"values": r(values), "swap": swap is not None})
    return {"dt": 1 / 60, "frames": frames}


def director_cases():
    params = json.loads((Path(__file__).parent / "director_params.json").read_text())
    params = {p["id"]: p for p in params}
    schedule = [(0, 0), (5, 2), (11, 6), (17, 10), (23, 13), (29, 7), (34, 1), (38, 0)]
    out = []
    for seed, sides in ((7, ("right", "left")), (42, ("left", "right"))):
        d = Director(params, (), seed=seed, arm_out=sides[0], arm_book=sides[1], orb=1.0, celebrate=False)
        frames = []
        for n in range(60 * 42):
            t = n / 60
            level = [lv for start, lv in schedule if t >= start][-1]
            d.set_level(level)
            values = d.update(1 / 60)
            if n % 10 == 0:
                frames.append({"n": n, "level": level, "gesture": d.last_gesture if d.gesture else None,
                               "values": {k: round(v, 7) for k, v in sorted(values.items())}})
        out.append({"seed": seed, "armOut": sides[0], "armBook": sides[1], "orb": 1.0, "frames": frames})
    return {"about": "Director stepped every 1/60 s from t=0 with level = the last schedule entry at or before t; "
                     "frames hold every 10th step (n counts steps, 0 is the first update).",
            "params": list(params.values()), "schedule": schedule, "cases": out}


PROFILES = [
    {"name": "flutist", "seed": 11, "sides": ("right", "left"),
     "profile": {"tempo": 0.8, "energy": 0.8, "sway": 1.2, "bounce": 0.4, "gap": 1.3, "idle": "rest",
                 "signature": ["sway", "hum", "tiptoe", "curtsy", "wingflap"], "hands": ["ripple", "flex"],
                 "poseHands": {"cruise": "flute", "boost": "flute"}, "exclude": ["run", "hops", "jump", "jumpjoy"]}},
    {"name": "seated", "seed": 5, "sides": ("left", "right"),
     "profile": {"tempo": 0.8, "energy": 0.75, "sway": 0.6, "bounce": 0.0, "gap": 1.3, "idle": "read",
                 "seated": True, "talk": False, "signature": ["nod", "tilt", "yawn", "hum", "spin"],
                 "hands": ["ripple", "flex"], "poseHands": {"cruise": "abacus", "redline": "fanflutter"}}},
    {"name": "cat", "seed": 23, "sides": ("right", "left"),
     "profile": {"tempo": 1.15, "energy": 1.15, "sway": 1.0, "bounce": 1.3, "gap": 0.85, "idle": "rest",
                 "signature": ["hop", "hops", "tilt", "eartwitch", "tailswish", "pounce", "floatbob", "swagger"],
                 "hands": ["flex", "fan", "count", "sleeveflop"]}},
]
# The pose on screen per time (seconds): the side change a pose swap makes, with its band for the hand set.
POSE_SIDES = [(6.0, "cruise"), (12.0, "boost"), (18.0, "overdrive"), (24.0, "redline"), (30.0, "boost"),
              (35.0, "cruise"), (39.0, "idle")]


def director_profile_cases():
    params = json.loads((Path(__file__).parent / "director_params.json").read_text())
    for pid, lo, default in (("TailSwish", -1, 0), ("WingFlap", -1, 0), ("EarTwitch.left", -1, 0),
                             ("EarTwitch.right", -1, 0)):
        params.append({"id": pid, "min": lo, "max": 1, "default": default})
    params = {p["id"]: p for p in params}
    schedule = [(0, 0), (5, 2), (11, 6), (17, 10), (23, 13), (29, 7), (34, 1), (38, 0)]
    out = []
    for case in PROFILES:
        arm_out, arm_book = case["sides"]
        d = Director(params, (), seed=case["seed"], arm_out=arm_out, arm_book=arm_book, orb=-1.0, celebrate=False,
                     profile=case["profile"])
        frames, sides = [], list(POSE_SIDES)
        for n in range(60 * 42):
            t = n / 60
            level = [lv for start, lv in schedule if t >= start][-1]
            d.set_level(level)
            if sides and t >= sides[0][0]:
                d.set_sides(arm_out, arm_book, pose_band=sides.pop(0)[1])
            values = d.update(1 / 60)
            if n % 10 == 0:
                frames.append({"n": n, "level": level, "gesture": d.last_gesture if d.gesture else None,
                               "values": {k: round(v, 7) for k, v in sorted(values.items())}})
        out.append({"name": case["name"], "seed": case["seed"], "armOut": arm_out, "armBook": arm_book, "orb": -1.0,
                    "profile": case["profile"], "frames": frames})
    return {"about": "As director.json, with a motion profile per case; at each poseSides time (before that step's "
                     "update, after set_level) set_sides(armOut, armBook, poseBand) runs once.",
            "params": list(params.values()), "schedule": schedule, "poseSides": POSE_SIDES, "cases": out}


FILES = {"rng.json": rng_cases, "curves.json": curve_cases, "springs.json": spring_cases, "orbits.json": orbit_cases,
         "rig.json": rig_cases, "physics.json": physics_cases, "odometer.json": odometer_cases,
         "switch.json": switch_cases, "director.json": director_cases,
         "director-profiles.json": director_profile_cases}


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args(argv)
    OUT.mkdir(parents=True, exist_ok=True)
    stale = []
    for name, make in FILES.items():
        text = json.dumps(make(), ensure_ascii=False, separators=(",", ":")) + "\n"
        path = OUT / name
        if args.check:
            if not path.exists() or path.read_text() != text:
                stale.append(name)
        else:
            path.write_text(text)
    if stale:
        print("stale fixtures: " + ", ".join(stale), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
