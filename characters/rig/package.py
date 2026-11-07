"""The character package: rig.json and atlas.png."""
import json
from pathlib import Path

import numpy as np
from PIL import Image

FORMAT = "agent-speed-character"
VERSION = 1


def write(out_dir, name, canvas, atlas, bones, params, drivers, physics, parts, landmarks):
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
        "parts": parts,
    }
    (out / "rig.json").write_text(json.dumps(doc, separators=(",", ":")))
    return out / "rig.json"


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
        "weights": np.round(weights, 4).ravel().tolist(),
    }


def read(path):
    path = Path(path)
    doc = json.loads((path / "rig.json").read_text())
    if doc.get("format") != FORMAT or doc.get("version") != VERSION:
        raise ValueError(f"not an {FORMAT} v{VERSION} package: {path}")
    atlas = np.asarray(Image.open(path / doc["atlas"]["file"]).convert("RGBA"))
    return doc, atlas
