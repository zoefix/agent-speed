"""Pose and expression variants: generated images aligned to the base, split by See-Through, grafted as parts.

An arm variant replaces one arm (and the held object when it is the object arm); a face variant replaces the eyes,
eyebrows and mouth. Each variant has a parameter `Var.<id>` (0..1) that fades its parts in and the replaced parts out.
"""
import json
import math
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

from . import layers as L
from . import pose, register, skeleton
from .params import BEND_RANGE, LIFT_RANGE, VARIANT_ARM_RANGE, VARIANT_FOREARM_RANGE

FACE_TAGS = ("eyewhite", "irides", "eyelash", "eyebrow", "mouth")


@dataclass
class Variant:
    id: str
    kind: str
    side: str | None
    parts: list
    replaces: list
    front: bool = False
    arm: dict | None = None
    report: dict = field(default_factory=dict)
    lead_deg: float = 0.0
    approach_deg: float = 0.0
    lead_fore_deg: float = 0.0
    approach_fore_deg: float = 0.0
    backdrop: dict | None = None


def load_manifest(path):
    path = Path(path)
    doc = json.loads(path.read_text())
    out = []
    for spec in doc["variants"]:
        spec = dict(spec)
        for key in ("psd", "image"):
            spec[key] = str((path.parent / spec[key]).resolve())
        out.append(spec)
    return out


def _compose(outer, inner):
    a = np.vstack([outer, [0, 0, 1]])
    b = np.vstack([inner, [0, 0, 1]])
    return (a @ b)[:2]


def _opaque(rgb):
    return np.concatenate([rgb, np.ones(rgb.shape[:2] + (1,), np.float32)], axis=2)


def _aligned(spec, base_reference, size, center_x, head_box=None):
    vsize, vlayers = L.load_psd(spec["psd"])
    if tuple(vsize) != tuple(size):
        raise ValueError(f"{spec['id']}: PSD canvas {vsize} differs from the base {size}")
    image = L.load_reference(spec["image"], size)
    matrix, report = register.similarity(image, base_reference)
    if head_box is not None:
        x0, y0, x1, y1 = (int(v) for v in head_box)
        coarse = register.warp_rgba(_opaque(image), matrix, size)[..., :3]
        local, local_report = register.similarity(coarse[y0:y1, x0:x1], base_reference[y0:y1, x0:x1])
        shift = np.array([[1, 0, x0], [0, 1, y0]], np.float64)
        unshift = np.array([[1, 0, -x0], [0, 1, -y0]], np.float64)
        matrix = _compose(shift, _compose(local, _compose(unshift, matrix)))
        report["head"] = local_report
    warped = register.warp_rgba(_opaque(image), matrix, size)
    image = warped[..., :3] * warped[..., 3:4] + (1 - warped[..., 3:4])
    for layer in vlayers:
        layer.rgba = register.warp_rgba(layer.rgba, matrix, size)
    return L.split_pairs(vlayers, center_x), image, report, matrix


def _angle(a, b):
    return math.degrees(math.atan2(b[1] - a[1], b[0] - a[0]))


def _wrap(d):
    return (d + 180) % 360 - 180


def _backdrop(ordered, owner):
    rgb = np.zeros(owner.shape + (3,), np.float32)
    for i, layer in enumerate(ordered):
        mine = owner == i
        rgb[mine] = layer.rgba[..., :3][mine]
    return {"names": [layer.name for layer in ordered], "tags": [layer.tag for layer in ordered], "owner": owner,
            "rgb": rgb, "coverage": L.coverage(ordered),
            "hidden": L.coverage([layer for layer in ordered if layer.tag in L.HIDDEN_TAGS])}


def _meet(gap, base_range, variant_range):
    """Splits an angle gap (base minus variant) so the old arm turns toward the new one and the new one starts
    where they meet; returns (base turn, variant offset)."""
    approach = float(np.clip(gap / 2, -variant_range, variant_range))
    lead = float(np.clip(approach - gap, -base_range, base_range))
    return lead, float(np.clip(gap + lead, -variant_range, variant_range))


def _rename(layer, vid):
    return L.Layer(f"{layer.name}@{vid}", layer.tag, layer.rgba, layer.side)


def arm_variant(spec, base_reference, size, lm, object_side):
    vid, side = spec["id"], spec["side"]
    fx0, fy0, fx1, fy1 = lm["face"]
    center_x = (fx0 + fx1) / 2
    vlayers, image, report, matrix = _aligned(spec, base_reference, size, center_x)
    arm = next((layer for layer in vlayers if layer.name == f"handwear.{side}"), None)
    if arm is None or arm.alpha.max() < 0.3:
        raise ValueError(f"{vid}: no handwear.{side} layer")
    holds = side == object_side
    held = [layer for layer in vlayers if layer.tag == "objects" and layer.alpha.max() > 0.3] if holds else []
    take = held + [arm]
    top = skeleton.box(arm.alpha, 0.3)[1]
    front = spec.get("front", top < lm["chin"] + 0.3 * (fy1 - fy0))
    ordered = [layer for layer in vlayers if layer not in take] + take if front else vlayers
    owner = L.reproject(ordered, image)
    backdrop = _backdrop(ordered, owner)

    base_arm = lm["arms"][side]
    joints = pose.arms(spec["image"], size, center_x, matrix).get(side)
    found = skeleton.arm_from_joints(joints) if joints else skeleton.find_arm(arm.alpha, anchor=base_arm["shoulder"])
    report["pose"] = bool(joints)
    upper_b, upper_v = _angle(base_arm["shoulder"], base_arm["elbow"]), _angle(found["shoulder"], found["elbow"])
    d_upper = _wrap(upper_b - upper_v)
    d_fore = _wrap(_wrap(_angle(base_arm["elbow"], base_arm["hand"]) - upper_b)
                   - _wrap(_angle(found["elbow"], found["hand"]) - upper_v))
    replaces = [f"handwear.{side}", f"hand.{side}"] + (["objects"] if holds else [])
    lead, approach = _meet(d_upper, LIFT_RANGE, VARIANT_ARM_RANGE)
    lead_fore, approach_fore = _meet(d_fore, BEND_RANGE, VARIANT_FOREARM_RANGE)
    report["gap_deg"] = [round(d_upper + lead - approach, 1), round(d_fore + lead_fore - approach_fore, 1)]
    return Variant(vid, "arm", side, [_rename(layer, vid) for layer in take], replaces, front, found, report,
                   lead_deg=lead, approach_deg=approach, lead_fore_deg=lead_fore, approach_fore_deg=approach_fore,
                   backdrop=backdrop)


def face_variant(spec, base_reference, size, lm, base_names):
    vid = spec["id"]
    fx0, fy0, fx1, fy1 = lm["face"]
    fw, fh = fx1 - fx0, fy1 - fy0
    head = (max(0, fx0 - 0.6 * fw), max(0, fy0 - 0.5 * fh), min(size[0], fx1 + 0.6 * fw), min(size[1], fy1 + 0.4 * fh))
    vlayers, image, report, _ = _aligned(spec, base_reference, size, (fx0 + fx1) / 2, head_box=head)
    L.reproject(vlayers, image)
    x0, y0, x1, y1 = (int(v) for v in head)
    take = []
    for layer in vlayers:
        if layer.tag not in FACE_TAGS:
            continue
        inside = layer.alpha[y0:y1, x0:x1]
        if inside.max() < 0.2:
            continue
        rgba = np.zeros_like(layer.rgba)
        rgba[y0:y1, x0:x1] = layer.rgba[y0:y1, x0:x1]
        take.append(_rename(L.Layer(layer.name, layer.tag, rgba, layer.side), vid))
    replaces = [n for n in base_names if n.split(".")[0] in FACE_TAGS]
    return Variant(vid, "face", None, take, replaces, report=report)


def load(spec, base_reference, size, lm, object_side, base_names):
    if spec["kind"] == "arm":
        return arm_variant(spec, base_reference, size, lm, object_side)
    if spec["kind"] == "face":
        return face_variant(spec, base_reference, size, lm, base_names)
    raise ValueError(f"unknown variant kind {spec['kind']}")
