"""Tails, wings and animal ears: landmarks, bones and skinning weights (spec/CHARACTERS.md, Appendages).

See-Through sometimes leaves an appendage out of every layer (DeepSeek's whale tail); `recover_missing` turns the
art no layer covers into a layer of its own, tagged from the character's hints, so it still draws and can move.
A tail is a chain of three bones along its midline from the root where it meets the body; each wing turns about its
root next to the spine; each animal ear turns about its base.
"""
import numpy as np
from scipy import ndimage
from skimage.graph import MCP_Geometric

from .layers import DRAW_ORDER, Layer, coverage

TAIL_BONES = ("tail1", "tail2", "tail3")


def _smoothstep(e0, e1, x):
    t = np.clip((np.asarray(x, np.float64) - e0) / max(e1 - e0, 1e-6), 0, 1)
    return t * t * (3 - 2 * t)


def foreground(reference, darkness=0.05, bare=None):
    """The drawn figure on the white canvas: anything not white, with areas enclosed by line art filled in, except
    the `bare` paper (matte.paper) that shows through it."""
    ink = (1 - reference.min(axis=2)) > darkness
    closed = ndimage.binary_closing(ink, iterations=3)
    filled = ndimage.binary_fill_holes(closed)
    return filled if bare is None else filled & ~bare


def recover_missing(parts, reference, hints, min_share=0.0005, bare=None, min_limb=0.4):
    """Adds a layer for each sizeable blob of the art that no layer covers. With a `tail` hint the largest blob below
    the chin becomes the tail (when no layer is tagged tail), with `wings` the largest blob below the eyes becomes the
    wings, either only when it is at least `min_limb` of the face's box (a scrap of dress must not flap); blobs by the
    head become headwear (above the face) or ears (beside it), so they turn with the head; any other blob joins the
    back as `extra`. Returns the names added."""
    h, w = reference.shape[:2]
    covered = coverage(parts) > 0.5
    missing = foreground(reference, bare=bare) & ~ndimage.binary_dilation(covered, iterations=2)
    missing = ndimage.binary_opening(missing, iterations=2)
    labels, count = ndimage.label(missing)
    if count == 0:
        return []
    sizes = ndimage.sum(missing, labels, range(1, count + 1))
    keep = [i + 1 for i, s in enumerate(sizes) if s >= min_share * h * w]
    tags = {p.tag for p in parts}
    face = next((p for p in parts if p.tag == "face"), None)
    if face is not None:
        fys, fxs = np.nonzero(face.alpha > 0.5)
        fx0, fx1, fy0, chin = fxs.min(), fxs.max(), fys.min(), fys.max()
    else:
        fx0, fx1, fy0, chin = 0.4 * w, 0.6 * w, 0.05 * h, 0.25 * h
    fw = fx1 - fx0
    limb = min_limb * fw * (chin - fy0)
    blobs = sorted(keep, key=lambda i: -sizes[i - 1])
    added = []
    for i in blobs:
        blob = ndimage.binary_dilation(labels == i, iterations=2)
        ys, xs = np.nonzero(blob)
        cy, cx = ys.mean(), xs.mean()
        by_head = cy < chin and fx0 - 1.2 * fw < cx < fx1 + 1.2 * fw
        big = sizes[i - 1] >= limb
        if hints.get("tail") and "tail" not in tags and cy > chin and big:
            tag = "tail"
        elif hints.get("wings") and "wings" not in tags and cy > fy0 + 0.5 * (chin - fy0) and not by_head and big:
            tag = "wings"
        elif by_head:
            tag = "headwear" if cy < fy0 else "ears"
        else:
            tag = "extra"
        rgba = np.zeros((h, w, 4), np.float32)
        rgba[..., :3] = reference
        # Inside, the blob is opaque; on its rim the art was drawn over white, so its darkness is its coverage.
        inner = ndimage.binary_erosion(blob, iterations=3)
        rim = np.clip((1 - reference.min(axis=2)) / 0.3, 0, 1)
        rgba[..., 3] = np.where(inner, 1.0, np.where(blob, rim, 0.0))
        names = {p.name for p in parts}
        name, n = tag, 1
        while name in names:
            n += 1
            name = f"{tag}{n}"
        layer = Layer(name, tag, rgba)
        slot = DRAW_ORDER.index("tail" if tag == "extra" else tag)
        at = next((k for k, p in enumerate(parts) if p.tag in DRAW_ORDER and DRAW_ORDER.index(p.tag) > slot),
                  len(parts))
        parts.insert(at, layer)
        tags.add(tag)
        added.append(name)
    return added


def _geodesic(mask, start):
    """Geodesic distance from `start` (x, y) inside `mask` (gaps of a few pixels bridged), and the farthest point."""
    ys, xs = np.nonzero(mask)
    y0, y1, x0, x1 = ys.min(), ys.max() + 1, xs.min(), xs.max() + 1
    crop = mask[y0:y1, x0:x1]
    bridged = ndimage.binary_closing(crop, iterations=3) | crop
    mcp = MCP_Geometric(np.where(bridged, 1.0, 40.0))
    s = (int(round(start[1])) - y0, int(round(start[0])) - x0)
    dist, _ = mcp.find_costs([s])
    dist = np.where(crop, dist, np.nan)
    end = np.unravel_index(np.nanargmax(dist), dist.shape)
    path = np.array(mcp.traceback(end), np.float64)[:, ::-1] + [x0, y0]
    field = np.full(mask.shape, np.nan, np.float32)
    field[y0:y1, x0:x1] = dist
    return field, path


def tail_landmarks(tail, parts, waist):
    """Root (where the tail meets the body, nearest the waist), the midline path to the tip and its length."""
    mask = tail.alpha > 0.5
    others = coverage([p for p in parts if p is not tail and p.tag not in ("back hair", "extra")]) > 0.5
    touching = mask & ndimage.binary_dilation(others, iterations=4)
    ys, xs = np.nonzero(touching if touching.any() else mask)
    k = np.argmin(np.hypot(xs - waist[0], ys - waist[1]))
    root = (float(xs[k]), float(ys[k]))
    field, path = _geodesic(mask, root)
    steps = np.hypot(*np.diff(path, axis=0).T)
    along = np.concatenate([[0.0], np.cumsum(steps)])
    length = float(along[-1])
    pivots = [tuple(path[np.searchsorted(along, f * length)]) for f in (0.0, 1 / 3, 2 / 3)]
    ends = [tuple(path[np.searchsorted(along, f * length)]) for f in (1 / 3, 2 / 3)] + [tuple(path[-1])]
    return {"root": root, "pivots": pivots, "ends": ends, "length": max(length, 1.0), "field": field}


def tail_weights(vertices, tail_lm, attach="lower"):
    field = tail_lm["field"]
    h, w = field.shape
    known = ~np.isnan(field)
    _, (iy, ix) = ndimage.distance_transform_edt(~known, return_indices=True)
    xi = np.clip(vertices[:, 0].astype(int), 0, w - 1)
    yi = np.clip(vertices[:, 1].astype(int), 0, h - 1)
    u = field[iy[yi, xi], ix[yi, xi]] / tail_lm["length"]
    w3 = _smoothstep(0.58, 0.75, u)
    w2 = _smoothstep(0.25, 0.42, u) * (1 - w3)
    base = 1 - w2 - w3
    stuck = 1 - _smoothstep(0.0, 0.1, u)
    return [(attach, base * stuck), ("tail1", base * (1 - stuck)), ("tail2", w2), ("tail3", w3)]


def wing_landmarks(wings, spine):
    """Per image side, the wing's root (its pixel nearest the spine point) and reach (farthest pixel from it)."""
    mask = np.zeros(wings[0].alpha.shape, bool)
    for layer in wings:
        mask |= layer.alpha > 0.5
    ys, xs = np.nonzero(mask)
    out = {}
    for side, pick in (("left", xs < spine[0]), ("right", xs >= spine[0])):
        if pick.sum() < 200:
            continue
        sx, sy = xs[pick], ys[pick]
        k = np.argmin(np.hypot(sx - spine[0], sy - spine[1]))
        root = (float(sx[k]), float(sy[k]))
        reach = float(np.hypot(sx - root[0], sy - root[1]).max())
        out[side] = {"root": root, "reach": max(reach, 1.0)}
    return out


def wing_weights(vertices, wing_lm, spine):
    out = []
    left = vertices[:, 0] < spine[0]
    chest = np.ones(len(vertices))
    for side, lm in wing_lm.items():
        mine = left if side == "left" else ~left
        u = np.hypot(vertices[:, 0] - lm["root"][0], vertices[:, 1] - lm["root"][1]) / lm["reach"]
        wing = _smoothstep(0.03, 0.15, u) * mine
        out.append((f"wing.{side}", wing))
        chest -= wing
    out.append(("chest", chest))
    return out


def animal_ears(parts, face_box):
    """Ears drawn above the face (cat, fox): per side the layer and the base point the ear turns about."""
    fx0, fy0, fx1, fy1 = face_box
    out = {}
    for layer in parts:
        if layer.tag != "ears":
            continue
        ys, xs = np.nonzero(layer.alpha > 0.5)
        if len(ys) < 80 or ys.mean() > fy0 + 0.25 * (fy1 - fy0):
            continue
        side = layer.side or ("left" if xs.mean() < (fx0 + fx1) / 2 else "right")
        base_y = ys.max()
        near = ys > base_y - 0.15 * (base_y - ys.min() + 1)
        out[side] = {"layer": layer.name, "base": (float(xs[near].mean()), float(base_y)),
                     "tip": (float(xs[np.argmin(ys)]), float(ys.min()))}
    return out
