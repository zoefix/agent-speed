"""Checks that refuse a pose instead of shipping it broken (spec/CHARACTERS.md, Checks).

A pose must draw feet when the character's idle pose does: See-Through can leave the boots of one pose out of every
layer, and that pose would stand on her hem. And the rig at rest must match the art: the rest check is the mean
absolute difference over the canvas (0 to 255) and the share of pixels off by more than 32. Every sound pose built
so far stays at or under 0.82 and 0.40 %; the pose that lost its boots reads 1.16 and 0.70 %.
"""
import json
from pathlib import Path

import numpy as np

REST_LIMITS = {"mean_abs_diff": 1.0, "px_diff_gt_32_pct": 0.55}
FEET = "footwear"


def rest_metrics(frame, reference):
    """The rest check of a frame (RGB in 0..1) against the art."""
    diff = np.abs(np.asarray(frame, np.float32) - reference).mean(axis=2) * 255
    return {"mean_abs_diff": round(float(diff.mean()), 3),
            "px_diff_gt_32_pct": round(float((diff > 32).mean() * 100), 3)}


def rest_problems(metrics, limits=REST_LIMITS):
    return [f"the rest check's {key} is {metrics[key]}, over {limit}: the rig at rest does not match the art "
            f"(a part the split left out?)" for key, limit in limits.items() if metrics[key] > limit]


def has_feet(bottoms, height, below=0.02):
    """Whether a pose draws feet: a footwear part, or legwear reaching at least `below` of the canvas height past the
    hem of the skirt (of the top without one), as tall boots or talons the split drew with the legs. `bottoms` maps
    each tag to the lowest row its parts draw."""
    if FEET in bottoms:
        return True
    if "legwear" not in bottoms:
        return False
    hem = bottoms.get("bottomwear", bottoms.get("topwear"))
    return hem is None or bottoms["legwear"] >= hem + below * height


def layer_bottoms(parts, solid=0.5):
    out = {}
    for layer in parts:
        rows = np.nonzero((layer.alpha > solid).any(axis=1))[0]
        if len(rows):
            out[layer.tag] = max(out.get(layer.tag, 0), int(rows.max()))
    return out


def pose_has_feet(pose_dir):
    """has_feet of a built pose (its rig.json), from the lowest vertex of each part."""
    doc = json.loads((Path(pose_dir) / "rig.json").read_text())
    bottoms = {}
    for part in doc["parts"]:
        ys = part["vertices"][1::2]
        if ys:
            bottoms[part["tag"]] = max(bottoms.get(part["tag"], 0), max(ys))
    return has_feet(bottoms, doc["canvas"][1])


def feet_problems(idle_feet, pose_feet):
    if idle_feet and not pose_feet:
        return ["the idle pose has feet and this pose draws none (no footwear, no legwear below the hem): the split "
                "left the shoes out of every layer and the build could not recover them, so she would stand on her "
                "hem"]
    return []
