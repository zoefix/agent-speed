"""Landmarks, bones and skinning weights derived from the part geometry.

Sides are image sides: "left" is the left half of the picture (the character's right hand).
"""
from dataclasses import dataclass

import numpy as np
from scipy import ndimage
from skimage.graph import MCP_Geometric


@dataclass
class Bone:
    name: str
    parent: str | None
    pivot: tuple


def box(alpha, threshold=0.12):
    ys, xs = np.nonzero(alpha > threshold)
    return float(xs.min()), float(ys.min()), float(xs.max() + 1), float(ys.max() + 1)


def smoothstep(e0, e1, x):
    t = np.clip((np.asarray(x, np.float64) - e0) / max(e1 - e0, 1e-6), 0, 1)
    return t * t * (3 - 2 * t)


def find_arm(alpha, anchor=None):
    """Shoulder, elbow and hand of an arm mask; `anchor` (the base shoulder) picks the shoulder of a raised arm."""
    mask = alpha > 0.3
    x0, y0, x1, y1 = (int(v) for v in box(alpha, 0.3))
    crop = mask[y0:y1, x0:x1]
    if anchor is None:
        top_rows = crop[: max(2, int(0.08 * crop.shape[0]))]
        ty, tx = np.nonzero(top_rows)
        shoulder = (x0 + tx.mean(), y0 + ty.mean())
    else:
        pts = np.argwhere(crop)
        near = pts[np.argsort(np.hypot(pts[:, 1] + x0 - anchor[0], pts[:, 0] + y0 - anchor[1]))[:40]]
        shoulder = (x0 + near[:, 1].mean(), y0 + near[:, 0].mean())
    bridged = ndimage.binary_closing(alpha[y0:y1, x0:x1] > 0.05, iterations=4) | crop
    cost = np.where(bridged, 1.0, 40.0)
    mcp = MCP_Geometric(cost)
    start = (int(round(shoulder[1] - y0)), int(round(shoulder[0] - x0)))
    if not crop[start]:
        sy, sx = np.argwhere(crop)[np.argmin(np.hypot(*(np.argwhere(crop) - start).T))]
        start = (int(sy), int(sx))
    dist, _ = mcp.find_costs([start])
    # Pieces the path cannot reach (a held book drawn apart from the sleeve) take no part in the arm's length.
    dist = np.where(crop & np.isfinite(dist), dist, np.nan)
    reach = float(np.nanmax(dist))
    off = np.abs(dist - 0.5 * reach)
    near = off < 0.04 * reach
    if not near.any():
        # The path jumped a gap past the half way: the pixels closest to it.
        near = off <= np.partition(off[np.isfinite(off)], 19)[19]
    ey, ex = np.nonzero(near)
    elbow = (x0 + ex.mean(), y0 + ey.mean())
    hy, hx = np.nonzero(dist > 0.9 * reach)
    hand = (x0 + hx.mean(), y0 + hy.mean())
    field = np.full(alpha.shape, np.nan, np.float32)
    field[y0:y1, x0:x1] = dist
    return {"shoulder": shoulder, "elbow": elbow, "hand": hand, "reach": reach, "field": field}


def arm_from_joints(joints):
    """Arm landmarks from pose joints; the hand tip extends the forearm past the wrist."""
    sh, el, wr = (np.array(joints[k], np.float64) for k in ("shoulder", "elbow", "wrist"))
    tip = wr + 0.6 * (wr - el)
    reach = float(np.linalg.norm(el - sh) + np.linalg.norm(tip - el))
    return {"shoulder": tuple(sh), "elbow": tuple(el), "wrist": tuple(wr), "hand": tuple(wr), "tip": tuple(tip),
            "reach": reach, "field": None}


def _one_foot_raised(feet, leg, share=0.15):
    """Whether the shoes are separate feet whose soles differ by more than `share` of the leg (a foot kicked up
    behind): one foot bone for both, pivoting between them, would tear the raised leg from the skirt."""
    solid = np.any([f.alpha > 0.3 for f in feet], axis=0)
    labels, count = ndimage.label(solid)
    if count < 2:
        return False
    sizes = ndimage.sum(solid, labels, range(1, count + 1))
    big = [i + 1 for i, size in enumerate(sizes) if size >= 0.15 * sizes.sum()]
    soles = [np.nonzero(labels == i)[0].max() for i in big]
    return len(soles) > 1 and max(soles) - min(soles) > share * leg


def _segment(points, a, b):
    ab = b - a
    t = np.clip(((points - a) @ ab) / max(float(ab @ ab), 1e-9), 0, 1)
    return np.linalg.norm(points - (a + t[:, None] * ab), axis=1), t


def arm_weights(vertices, arm):
    """(chest, upper arm, forearm) weights from the distance to the two arm segments."""
    sh, el, tip = (np.array(arm[k], np.float64) for k in ("shoulder", "elbow", "tip"))
    d_upper, t_upper = _segment(vertices, sh, el)
    d_fore, _ = _segment(vertices, el, tip)
    soft = 0.07 * max(float(np.linalg.norm(el - sh)), 1.0)
    fore = 1 / (1 + np.exp(np.clip((d_fore - d_upper) / soft, -40, 40)))
    attached = smoothstep(0.0, 0.22, t_upper)
    return (1 - fore) * (1 - attached), (1 - fore) * attached, fore


# The waist sits this far from the shoulders to the hips (pose.torso) when the layers place it outside WAIST_RANGE.
WAIST_SHARE = 0.65
WAIST_RANGE = (0.5, 0.9)


def landmarks(layers, torso=None):
    """Face, waist, feet, arms and eyes from the layers. The waist is where the top meets the bottom, unless `torso`
    (pose.torso) shows that lands off the body's waist: a skirt layer holding a cape from the shoulders, or a coat to
    the knees."""
    named = {layer.name: layer for layer in layers}
    tags = {}
    for layer in layers:
        tags.setdefault(layer.tag, []).append(layer)

    fx0, fy0, fx1, fy1 = box(named["face"].alpha)
    face_w, face_h = fx1 - fx0, fy1 - fy0
    chin = fy1
    neck_cx = (box(named["neck"].alpha)[0] + box(named["neck"].alpha)[2]) / 2 if "neck" in named else (fx0 + fx1) / 2
    lm = {
        "face": (fx0, fy0, fx1, fy1),
        "faceWidth": face_w,
        "chin": chin,
        "neck": (neck_cx, chin + 0.06 * face_h),
    }

    top = box(named["topwear"].alpha) if "topwear" in named else None
    bottom = box(named["bottomwear"].alpha) if "bottomwear" in named else None
    if top and bottom:
        waist_y = (top[3] + bottom[1]) / 2
        waist_x = (top[0] + top[2]) / 2
    elif top:
        waist_y, waist_x = top[3], (top[0] + top[2]) / 2
    else:
        waist_y, waist_x = chin + 2.2 * face_h, neck_cx
    if torso and torso["hip"] > torso["shoulder"]:
        span = torso["hip"] - torso["shoulder"]
        if not WAIST_RANGE[0] <= (waist_y - torso["shoulder"]) / span <= WAIST_RANGE[1]:
            waist_y = torso["shoulder"] + WAIST_SHARE * span
    lm["waist"] = (waist_x, waist_y)
    lowest = max(box(layer.alpha)[3] for layer in layers)
    feet_src = named.get("footwear") or named.get("bottomwear")
    feet_x = (box(feet_src.alpha)[0] + box(feet_src.alpha)[2]) / 2 if feet_src else waist_x
    lm["feet"] = (feet_x, lowest)
    lm["hem"] = bottom[3] if bottom else lowest
    lm["skirtMid"] = waist_y + 0.45 * (lm["hem"] - waist_y)
    if "footwear" in named and not _one_foot_raised(tags["footwear"], lowest - waist_y):
        bx0, by0, bx1, by1 = box(named["footwear"].alpha, 0.3)
        lm["foot"] = {"box": (bx0, by0, bx1, by1), "ankle": ((bx0 + bx1) / 2, by0)}

    lm["arms"] = {}
    for layer in tags.get("handwear", []):
        lm["arms"][layer.side or "single"] = find_arm(layer.alpha)

    lm["eyes"] = {}
    for side in ("left", "right"):
        parts = [named[n] for n in (f"eyewhite.{side}", f"eyelash.{side}") if n in named]
        if not parts:
            continue
        boxes = np.array([box(p.alpha, 0.2) for p in parts])
        x0, y0 = boxes[:, :2].min(axis=0)
        x1, y1 = boxes[:, 2:].max(axis=0)
        lm["eyes"][side] = {"box": (x0, y0, x1, y1), "pivot": ((x0 + x1) / 2, y0 + 0.62 * (y1 - y0))}

    if "back hair" in named:
        hx0, hy0, hx1, hy1 = box(named["back hair"].alpha)
        hcx = (hx0 + hx1) / 2
        lm["backHair"] = {"root": (hcx, chin), "mid": (hcx, chin + 0.45 * (hy1 - chin)), "end": hy1}
    if "front hair" in named:
        alpha = named["front hair"].alpha
        below = alpha[int(chin + 0.15 * face_h):] > 0.3
        if below.sum() > 0.02 * (alpha > 0.3).sum():
            ys, xs = np.nonzero(below)
            ys = ys + int(chin + 0.15 * face_h)
            top_y = ys.min()
            top_x = xs[ys < top_y + 12].mean()
            end_y = ys.max()
            mid_y = top_y + 0.45 * (end_y - top_y)
            mid_x = xs[np.abs(ys - mid_y) < 8].mean()
            lm["lock"] = {"root": (top_x, chin - 0.05 * face_h), "mid": (mid_x, mid_y), "end": float(end_y)}
    return lm


def bones(lm):
    fx0, fy0, fx1, fy1 = lm["face"]
    face_c = ((fx0 + fx1) / 2, (fy0 + fy1) / 2)
    eyes_y = np.mean([e["pivot"][1] for e in lm["eyes"].values()]) if lm["eyes"] else fy0 + 0.55 * (fy1 - fy0)
    out = [
        Bone("root", None, lm["feet"]),
        Bone("turn", "root", (lm["waist"][0], lm["feet"][1])),
        Bone("lower", "turn", lm["feet"]),
        Bone("skirt", "lower", lm["waist"]),
        Bone("skirtHem", "skirt", (lm["waist"][0], lm["skirtMid"])),
        Bone("chest", "lower", lm["waist"]),
        Bone("head", "chest", lm["neck"]),
        Bone("face", "head", face_c),
        Bone("features", "head", (face_c[0], eyes_y)),
        Bone("bangs", "head", (face_c[0], fy0)),
        Bone("side", "head", face_c),
        Bone("hairTop", "head", face_c),
    ]
    for side, eye in lm["eyes"].items():
        out.append(Bone(f"eye.{side}", "features", eye["pivot"]))
        out.append(Bone(f"iris.{side}", f"eye.{side}", eye["pivot"]))
    if "backHair" in lm:
        out.append(Bone("hairBack1", "chest", lm["backHair"]["root"]))
        out.append(Bone("hairBack2", "hairBack1", lm["backHair"]["mid"]))
    if "lock" in lm:
        out.append(Bone("lock1", "chest", lm["lock"]["root"]))
        out.append(Bone("lock2", "lock1", lm["lock"]["mid"]))
    for side, arm in lm["arms"].items():
        out.append(Bone(f"shoulder.{side}", "chest", arm["shoulder"]))
        out.append(Bone(f"arm.{side}", f"shoulder.{side}", arm["shoulder"]))
        out.append(Bone(f"forearm.{side}", f"arm.{side}", arm["elbow"]))
    if "foot" in lm:
        out.append(Bone("foot", "turn", lm["foot"]["ankle"]))
    return out


def weights_for(layer, vertices, lm, attach=None, arm=None, suffix=""):
    """Returns [(bone, weight array)] for one part; weights sum to 1 per vertex.

    `arm` and `suffix` select a variant arm (its own landmarks and bones such as `arm.right@wave`).
    """
    x, y = vertices[:, 0], vertices[:, 1]
    tag, side = layer.tag, layer.side
    fx0, fy0, fx1, fy1 = lm["face"]
    face_h = fy1 - fy0
    chin = lm["chin"]
    one = np.ones(len(vertices))

    if tag in ("eyewhite", "eyelash") and side:
        return [(f"eye.{side}", one)]
    if tag == "irides" and side:
        return [(f"iris.{side}", one)]
    if tag in ("eyebrow", "nose", "mouth", "eyewear"):
        return [("features", one)]
    if tag == "face":
        return [("face", one)]
    if tag in ("ears", "earwear", "headwear"):
        return [("side", one)]
    if tag == "neck":
        w = 1 - smoothstep(chin - 0.05 * face_h, chin + 0.35 * face_h, y)
        return [("head", w), ("chest", 1 - w)]
    if tag == "back hair" and "backHair" in lm:
        bh = lm["backHair"]
        w_top = 1 - smoothstep(chin - 0.3 * face_h, chin + 0.5 * face_h, y)
        w_2 = smoothstep(bh["mid"][1] - 0.2 * face_h, bh["mid"][1] + 0.6 * face_h, y) * (1 - w_top)
        return [("hairTop", w_top), ("hairBack1", 1 - w_top - w_2), ("hairBack2", w_2)]
    if tag == "front hair":
        if "lock" not in lm:
            return [("bangs", one)]
        lk = lm["lock"]
        w_bangs = 1 - smoothstep(chin - 0.25 * face_h, chin + 0.45 * face_h, y)
        w_2 = smoothstep(lk["mid"][1] - 0.2 * face_h, lk["mid"][1] + 0.6 * face_h, y) * (1 - w_bangs)
        return [("bangs", w_bangs), ("lock1", 1 - w_bangs - w_2), ("lock2", w_2)]
    if tag == "handwear" and (arm is not None or (side or "single") in lm["arms"]):
        out = _arm_layer_weights(vertices, lm, side, arm, suffix)
        if lm.get("bodyInArms"):
            body = _body_share(vertices, lm, arm if arm is not None else lm["arms"][side or "single"])
            w_low = _below_waist(y, lm, face_h)
            out = [(bone, w * (1 - body)) for bone, w in out] + [("chest", body * (1 - w_low)), ("lower", body * w_low)]
        return out
    if tag == "objects" and attach:
        return [(attach, one)]
    if lm.get("rigidLower") and tag in ("footwear", "legwear", "bottomwear"):
        if tag == "bottomwear":
            w_low = _below_waist(y, lm, face_h)
            return [("chest", 1 - w_low), ("lower", w_low)]
        return [("lower", one)]
    if tag in ("footwear", "legwear") and lm.get("legsInSkirt"):
        return skirt_weights(y, lm, face_h)
    if tag in ("footwear", "legwear") and "foot" in lm:
        return [("foot", one)]
    if tag == "topwear":
        w_low = _below_waist(y, lm, face_h)
        return [("chest", 1 - w_low), ("lower", w_low)]
    if tag in ("neckwear", "wings", "tail", "objects", "handwear"):
        return [("chest", one)]
    if tag == "bottomwear":
        return skirt_weights(y, lm, face_h)
    return [("lower", one)]


def _arm_layer_weights(vertices, lm, side, arm, suffix):
    x, y = vertices[:, 0], vertices[:, 1]
    key = side or "single"
    arm = arm if arm is not None else lm["arms"][key]
    if arm.get("tip") is not None:
        chest, upper, fore = arm_weights(vertices, arm)
        return [(f"shoulder.{key}", chest), (f"arm.{key}{suffix}", upper), (f"forearm.{key}{suffix}", fore)]
    field = arm["field"]
    xi = np.clip(x.astype(int), 0, field.shape[1] - 1)
    yi = np.clip(y.astype(int), 0, field.shape[0] - 1)
    d = field[yi, xi]
    if np.isnan(d).any():
        sx, sy = arm["shoulder"]
        fallback = np.hypot(x - sx, y - sy)
        d = np.where(np.isnan(d), fallback, d)
    reach = arm["reach"]
    w_arm = smoothstep(0.0, 0.14 * reach, d)
    w_fore = smoothstep(0.38 * reach, 0.62 * reach, d)
    return [(f"shoulder.{key}", 1 - w_arm), (f"arm.{key}{suffix}", w_arm * (1 - w_fore)),
            (f"forearm.{key}{suffix}", w_arm * w_fore)]


def _body_share(vertices, lm, arm):
    """How much of an arm layer's vertex is body the split drew into it: inside the torso (between the shoulders,
    below the chin) and over 0.45 face widths from the arm's bones, or below the waist (a skirt)."""
    x, y = vertices[:, 0], vertices[:, 1]
    fx0, fy0, fx1, fy1 = lm["face"]
    face_w, face_h = fx1 - fx0, fy1 - fy0
    low = smoothstep(lm["waist"][1] + 0.2 * face_h, lm["waist"][1] + 1.2 * face_h, y)
    if arm.get("tip") is None:
        return low
    sh, el, tip = (np.array(arm[k], np.float64) for k in ("shoulder", "elbow", "tip"))
    d = np.minimum(_segment(vertices, sh, el)[0], _segment(vertices, el, tip)[0])
    far = smoothstep(0.45 * face_w, 0.9 * face_w, d)
    shoulders = sorted(a["shoulder"][0] for a in lm["arms"].values())
    x0, x1 = (shoulders[0], shoulders[-1]) if len(shoulders) > 1 else (fx0, fx1)
    m = 0.3 * face_w
    inside = smoothstep(x0 - m, x0 + m, x) * (1 - smoothstep(x1 - m, x1 + m, x))
    torso = inside * smoothstep(lm["chin"], lm["chin"] + 0.5 * face_h, y)
    return np.maximum(far * torso, low)


def _below_waist(y, lm, face_h):
    return smoothstep(lm["waist"][1] - 0.3 * face_h, lm["waist"][1] + 0.1 * face_h, y)


def skirt_weights(y, lm, face_h):
    """Above the waist the skirt layer (a cape from the shoulders) follows the chest as the top does; below it the
    skirt bends and swings most at the hem. Legs the art draws inside the skirt (sheer cloth, a slit) take the same
    weights, so the two drawings of a leg never part."""
    low = _below_waist(y, lm, face_h)
    w = smoothstep(lm["waist"][1], lm["hem"], y) ** 1.3
    h = smoothstep(lm.get("skirtMid", lm["hem"]), lm["hem"], y) ** 1.2
    return [("chest", 1 - low), ("lower", low * (1 - w)), ("skirt", low * w * (1 - h)), ("skirtHem", low * w * h)]


def holding_side(layer, lm, reach, samples=4000):
    """The side whose hand holds an object piece: the hand (wrist to fingertips, the hand's own keypoints, or the hand
    point of an arm the pose model missed) that comes within `reach` of the piece, nearest first; None when no hand
    does (a purse hanging from the waist)."""
    ys, xs = np.nonzero(layer.alpha > 0.3)
    if not len(xs):
        return None
    step = max(1, len(xs) // samples)
    pts = np.stack([xs[::step], ys[::step]], axis=1).astype(np.float64)
    best, best_d = None, reach
    for key, arm in lm["arms"].items():
        if arm.get("wrist") is not None and arm.get("tip") is not None:
            d = float(_segment(pts, np.array(arm["wrist"], np.float64), np.array(arm["tip"], np.float64))[0].min())
        elif arm.get("hand") is not None:
            d = float(np.linalg.norm(pts - np.array(arm["hand"], np.float64), axis=1).min())
        else:
            continue
        hand = lm.get("hands", {}).get(key)
        if hand is not None:
            keys = np.concatenate([[hand["wrist"]], np.reshape(hand["fingers"], (-1, 2))]).astype(np.float64)
            d = min(d, float(np.min(np.linalg.norm(pts[:, None, :] - keys[None, :, :], axis=2))))
        if d < best_d:
            best, best_d = key, d
    return best


def attach_object(layer, lm):
    """The forearm whose hand is closest to an object (a held book, fan, staff)."""
    if not lm["arms"]:
        return None
    ys, xs = np.nonzero(layer.alpha > 0.3)
    best, best_d = None, np.inf
    for key, arm in lm["arms"].items():
        if arm.get("wrist") is not None:
            cx, cy = arm["wrist"]
        else:
            hand = np.argwhere(arm["field"] > 0.6 * arm["reach"])
            if len(hand) == 0:
                continue
            cy, cx = hand.mean(axis=0)
        d = np.hypot(xs.mean() - cx, ys.mean() - cy)
        if d < best_d:
            best, best_d = f"forearm.{key}", d
    return best


@dataclass
class _Probe:
    tag: str
    side: str | None = None


def weights_by_owner(vertices, tag_map, lm):
    """Weights for a patch that mixes several layers: each vertex follows the layer that owns its pixel."""
    h, w = tag_map.shape
    xi = np.clip(vertices[:, 0].astype(int), 0, w - 1)
    yi = np.clip(vertices[:, 1].astype(int), 0, h - 1)
    tags = tag_map[yi, xi]
    tags = np.where(np.isin(tags, ["", "handwear", "objects"]), "topwear", tags)
    acc = {}
    for tag in np.unique(tags):
        mine = tags == tag
        for bone, weight in weights_for(_Probe(str(tag)), vertices, lm):
            acc.setdefault(bone, np.zeros(len(vertices)))[mine] += np.broadcast_to(weight, mine.shape)[mine]
    return list(acc.items())


def pack_weights(influences, bone_index, max_influences=3):
    names = [name for name, _ in influences]
    w = np.stack([np.asarray(v, np.float64) for _, v in influences], axis=1)
    w = np.clip(w, 0, None)
    order = np.argsort(-w, axis=1)[:, :max_influences]
    top = np.take_along_axis(w, order, axis=1)
    top /= np.clip(top.sum(axis=1, keepdims=True), 1e-9, None)
    idx = np.vectorize(lambda k: bone_index[names[k]])(order)
    return idx.astype(np.int32), top.astype(np.float32)
