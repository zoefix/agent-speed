"""The character package: rig.json and atlas.png."""
import json
from pathlib import Path

import numpy as np
from PIL import Image

FORMAT = "agent-speed-character"
VERSION = 1


def write(out_dir, name, canvas, atlas, bones, params, drivers, physics, parts, landmarks, part_drivers=(), variants=()):
    out = Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)
    Image.fromarray(atlas, "RGBA").save(out / "atlas.png", optimize=True)
    doc = {
        "format": FORMAT,
        "version": VERSION,
        "name": name,
        "canvas": list(canvas),
        "atlas": {"file": "atlas.png", "size": [atlas.shape[1], atlas.shape[0]]},
        "landmarks": landmarks,
        "bones": [{"name": b.name, "parent": b.parent, "pivot": [round(float(v), 2) for v in b.pivot]} for b in bones],
        "params": params,
        "drivers": drivers,
        "physics": physics,
        "partDrivers": list(part_drivers),
        "variants": list(variants),
        "parts": parts,
    }
    # A NaN would make the whole pack unreadable to the players: fail the build instead.
    (out / "rig.json").write_text(json.dumps(doc, separators=(",", ":"), allow_nan=False))
    return out / "rig.json"


def _exact_weights(weights):
    """Rounds to 4 decimals and moves the rounding error onto each vertex's largest weight, so rows sum to 1."""
    w = np.round(np.asarray(weights, np.float64), 4)
    rows = np.arange(len(w))
    top = np.argmax(w, axis=1)
    w[rows, top] = np.round(w[rows, top] + 1 - w.sum(axis=1), 4)
    return w


def part_entry(layer, order, vertices, uv, triangles, bone_idx, weights):
    return {
        "name": layer.name,
        "tag": layer.tag,
        "order": order,
        "vertices": np.round(vertices, 2).ravel().tolist(),
        "uv": np.round(uv, 2).ravel().tolist(),
        "triangles": triangles.ravel().tolist(),
        "influences": bone_idx.shape[1],
        "bones": bone_idx.ravel().tolist(),
        "weights": _exact_weights(weights).ravel().tolist(),
    }


def read(path):
    path = Path(path)
    doc = json.loads((path / "rig.json").read_text())
    if doc.get("format") != FORMAT or doc.get("version") != VERSION:
        raise ValueError(f"not an {FORMAT} v{VERSION} package: {path}")
    atlas = np.asarray(Image.open(path / doc["atlas"]["file"]).convert("RGBA"))
    return doc, atlas
