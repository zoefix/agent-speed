"""Evaluates a character package: parameter values -> bone transforms -> deformed part vertices."""
import numpy as np


def interpolate(keys, value):
    xs, ys = keys[:, 0], keys[:, 1]
    return float(np.interp(value, xs, ys))


class Rig:
    def __init__(self, doc):
        self.doc = doc
        self.canvas = tuple(doc["canvas"])
        self.params = {p["id"]: p for p in doc["params"]}
        self.bones = doc["bones"]
        self.index = {b["name"]: i for i, b in enumerate(self.bones)}
        self.parent = [self.index[b["parent"]] if b["parent"] else -1 for b in self.bones]
        if any(p >= i for i, p in enumerate(self.parent)):
            raise ValueError("bones must list parents first")
        self.pivot = np.array([b["pivot"] for b in self.bones], np.float64)
        self.drivers = [[] for _ in self.bones]
        for d in doc["drivers"]:
            self.drivers[self.index[d["bone"]]].append((d["param"], d["channel"], np.array(d["keys"], np.float64)))
        self.parts = []
        for p in sorted(doc["parts"], key=lambda p: p["order"]):
            k = p["influences"]
            self.parts.append({
                "name": p["name"],
                "tag": p["tag"],
                "vertices": np.array(p["vertices"], np.float64).reshape(-1, 2),
                "uv": np.array(p["uv"], np.float32).reshape(-1, 2),
                "triangles": np.array(p["triangles"], np.int32).reshape(-1, 3),
                "bones": np.array(p["bones"], np.int64).reshape(-1, k),
                "weights": np.array(p["weights"], np.float64).reshape(-1, k),
                "opacity": [],
            })
        by_name = {part["name"]: part for part in self.parts}
        for d in doc.get("partDrivers", []):
            if d["part"] in by_name:
                by_name[d["part"]]["opacity"].append((d["param"], np.array(d["keys"], np.float64)))
        self.variants = doc.get("variants", [])

    def defaults(self):
        return {pid: p["default"] for pid, p in self.params.items()}

    def clamp(self, values):
        out = self.defaults()
        for pid, v in values.items():
            if pid in self.params:
                p = self.params[pid]
                out[pid] = min(max(v, p["min"]), p["max"])
        return out

    def bone_matrices(self, values):
        values = self.clamp(values)
        world = np.zeros((len(self.bones), 3, 3))
        for i, drivers in enumerate(self.drivers):
            ch = {"rotate": 0.0, "x": 0.0, "y": 0.0, "scaleX": 0.0, "scaleY": 0.0}
            for pid, channel, keys in drivers:
                ch[channel] += interpolate(keys, values[pid])
            px, py = self.pivot[i]
            a = np.radians(ch["rotate"])
            c, s = np.cos(a), np.sin(a)
            sx, sy = 1 + ch["scaleX"], 1 + ch["scaleY"]
            local = np.array([
                [c * sx, -s * sy, 0.0],
                [s * sx, c * sy, 0.0],
                [0.0, 0.0, 1.0],
            ])
            local[:2, 2] = np.array([px, py]) - local[:2, :2] @ np.array([px, py]) + np.array([ch["x"], ch["y"]])
            world[i] = local if self.parent[i] < 0 else world[self.parent[i]] @ local
        return world

    def deform(self, world):
        out = []
        for part in self.parts:
            m = world[part["bones"]]
            v = part["vertices"]
            moved = np.einsum("nkij,nj->nki", m[..., :2, :2], v) + m[..., :2, 2]
            out.append((part["weights"][..., None] * moved).sum(axis=1))
        return out

    def opacities(self, values):
        values = self.clamp(values)
        out = []
        for part in self.parts:
            o = 1.0
            for pid, keys in part["opacity"]:
                o *= interpolate(keys, values[pid])
            out.append(o)
        return out

    def pose(self, values):
        return self.deform(self.bone_matrices(values))
