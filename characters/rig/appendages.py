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

SQUARE = np.ones((3, 3), bool)

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


def _missing(parts, reference, bare):
    """Blobs of the art no layer covers, 2 px clear of every layer and opened by 2 px: labels, sizes, the coverage
    mask and the drawn figure (foreground)."""
    covered = coverage(parts) > 0.5
    art = foreground(reference, bare=bare)
    missing = ndimage.binary_opening(art & ~ndimage.binary_dilation(covered, iterations=2), iterations=2)
    labels, count = ndimage.label(missing)
    return labels, np.asarray(ndimage.sum(missing, labels, range(1, count + 1))), covered, art


def _drawn(ids, labels, reference, figure, min_figure, max_chroma):
    """The blobs that are drawn figure: a grey blob the figure matte scores below `min_figure` on average is the
    shadow drawn on the floor."""
    if figure is None or not ids:
        return ids
    scores = np.atleast_1d(ndimage.mean(figure, labels, ids))
    chroma = np.atleast_1d(ndimage.mean(reference.max(axis=2) - reference.min(axis=2), labels, ids))
    return [i for i, score, c in zip(ids, scores, chroma) if score >= min_figure or c >= max_chroma]


def _touch(blob, masks, ring=4):
    """Share of a ring around `blob` that lies on any of `masks`."""
    band = ndimage.binary_dilation(blob, iterations=ring) & ~blob
    on = np.zeros_like(band)
    for m in masks:
        on |= m
    return float((band & on).sum()) / max(int(band.sum()), 1)


def _around(found, pad, shape):
    return tuple(slice(max(s.start - pad, 0), min(s.stop + pad, n)) for s, n in zip(found, shape))


def recover_missing(parts, reference, hints, min_share=0.0005, bare=None, min_limb=0.4, figure=None, min_figure=0.5,
                    max_chroma=0.15):
    """Adds a layer for each sizeable blob of the art that no layer covers. With a `tail` hint the largest blob below
    the chin becomes the tail (when no layer is tagged tail), with `wings` the largest blob below the eyes becomes the
    wings, either only when it is at least `min_limb` of the face's box (a scrap of dress must not flap); blobs by the
    head become headwear (above the face) or ears (beside it), so they turn with the head; when the split made no
    footwear, a blob reaching the bottom of the figure (within 2 % of the canvas) that touches the legwear is a shoe,
    `footwear`, so it moves with the leg; a blob no larger than the footwear with at least 15 % of its rim on it (a
    calf above a kicked-up slipper) is `legwear`; an `extra` blob no larger than the tail with at least 15 % of its
    rim on the tail (its base, cut short) joins the tail layer; any other blob joins the back as `extra`. A grey blob (mean chroma below `max_chroma`) the figure matte (matte.figure) scores below
    `min_figure` on average is the shadow drawn on the floor and stays out; a coloured one (a hologram) stays in.
    Returns the names added."""
    h, w = reference.shape[:2]
    labels, sizes, _, _ = _missing(parts, reference, bare)
    if not len(sizes):
        return []
    keep = [i + 1 for i, s in enumerate(sizes) if s >= min_share * h * w]
    keep = _drawn(keep, labels, reference, figure, min_figure, max_chroma)
    tags = {p.tag for p in parts}
    legs = [p.alpha > 0.5 for p in parts if p.tag == "legwear"] if "footwear" not in tags else []
    feet = [p.alpha > 0.5 for p in parts if p.tag == "footwear"]
    feet_area = sum(int(f.sum()) for f in feet)
    rows = np.nonzero(foreground(reference, bare=bare).any(axis=1))[0]
    bottom = rows.max() if len(rows) else h
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
        shoe = ys.max() >= bottom - 0.02 * h and any((ndimage.binary_dilation(blob, iterations=3) & leg).any()
                                                      for leg in legs)
        if shoe:
            tag = "footwear"
        elif hints.get("tail") and "tail" not in tags and cy > chin and big:
            tag = "tail"
        elif hints.get("wings") and "wings" not in tags and cy > fy0 + 0.5 * (chin - fy0) and not by_head and big:
            tag = "wings"
        elif by_head:
            tag = "headwear" if cy < fy0 else "ears"
        elif feet and blob.sum() <= feet_area and _touch(blob, feet) >= 0.15:
            tag = "legwear"
        else:
            tag = "extra"
        rgba = np.zeros((h, w, 4), np.float32)
        rgba[..., :3] = reference
        # Inside, the blob is opaque; on its rim the art was drawn over white, so its darkness is its coverage.
        inner = ndimage.binary_erosion(blob, iterations=3)
        rim = np.clip((1 - reference.min(axis=2)) / 0.3, 0, 1)
        rgba[..., 3] = np.where(inner, 1.0, np.where(blob, rim, 0.0))
        tail = next((p for p in parts if p.tag == "tail"), None)
        if (tag == "extra" and tail is not None and sizes[i - 1] <= (tail.alpha > 0.5).sum()
                and _touch(blob, [tail.alpha > 0.5]) >= 0.15):
            # The rest of a tail the split cut short (its base, its tip) joins the tail, so it swings with it.
            tail.rgba[..., :3] = np.where(blob[..., None], reference, tail.rgba[..., :3])
            tail.rgba[..., 3] = np.maximum(tail.alpha, rgba[..., 3])
            added.append(f"{tail.name}+")
            continue
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


def attach_fragments(parts, reference, bare=None, figure=None, min_share=0.0005, touch=12, enclosed=0.9, ring=3,
                     reach=12, min_figure=0.5, max_chroma=0.15):
    """Gives each blob too small for recover_missing that sticks out of the layers (not enclosed: fill_holes takes
    those) to the layer holding most of a ring around it, when that layer holds at least `touch` pixels of the ring
    (a long feather cut at its root meets the layer only there): See-Through cuts parts at the edge of the box it
    splits the head in, so the top of a crown or a hair ornament belongs to no layer. The piece is the whole patch
    of uncovered art the blob lies in (thin feather tips too), within `reach` px of the blob; opaque, and as opaque
    as the art is dark on its 1 px rim against the paper. Grey blobs the matte calls background stay out, as in
    recover_missing. Returns how many it attached."""
    labels, sizes, covered, art = _missing(parts, reference, bare)
    h, w = labels.shape
    small = _drawn([i + 1 for i, s in enumerate(sizes) if s < min_share * h * w], labels, reference, figure,
                   min_figure, max_chroma)
    darkness = np.clip((1 - reference.min(axis=2)) / 0.3, 0, 1)
    loose, _ = ndimage.label(art & ~covered)
    objects = ndimage.find_objects(labels)
    attached = 0
    for i in small:
        box = _around(objects[i - 1], reach, (h, w))
        ids = np.unique(loose[box][labels[box] == i])
        piece = np.isin(loose[box], ids[ids > 0])
        band = ndimage.binary_dilation(piece, SQUARE, iterations=ring) & ~piece
        if covered[box][band].mean() >= enclosed:
            continue
        held = [int((layer.alpha[box][band] > 0.5).sum()) for layer in parts]
        best = int(np.argmax(held))
        if held[best] < touch:
            continue
        rim = ndimage.binary_dilation(~(art[box] | covered[box]), SQUARE, iterations=1)
        alpha = np.where(rim, darkness[box], 1.0)
        view = parts[best].rgba[box]
        view[..., :3] = np.where(piece[..., None], reference[box], view[..., :3])
        view[..., 3] = np.where(piece, np.maximum(view[..., 3], alpha), view[..., 3])
        attached += 1
    return attached


def fill_holes(parts, reference, bare=None, enclosed=0.9, touch=0.15, ring=3, max_share=0.002):
    """Gives each patch of the art that no layer covers but layers surround (at least `enclosed` of a ring around it)
    to the lowest layer that holds at least `touch` of that ring: a gap between belt straps shows the robe behind
    them. Patches too small or thin for recover_missing would otherwise draw as white specks; larger ones it left out
    on purpose stay out. Returns the pixels filled."""
    covered = coverage(parts) > 0.5
    labels, _ = ndimage.label(foreground(reference, bare=bare) & ~covered)
    h, w = labels.shape
    filled = 0
    for i, found in enumerate(ndimage.find_objects(labels), 1):
        if found is None:
            continue
        box = _around(found, ring, (h, w))
        hole = labels[box] == i
        if hole.sum() > max_share * h * w:
            continue
        band = ndimage.binary_dilation(hole, SQUARE, iterations=ring) & ~hole
        if covered[box][band].mean() < enclosed:
            continue
        owner = next((layer for layer in parts if (layer.alpha[box][band] > 0.5).mean() >= touch), None)
        if owner is None:
            continue
        view = owner.rgba[box]
        view[..., :3] = np.where(hole[..., None], reference[box], view[..., :3])
        view[..., 3] = np.where(hole, 1.0, view[..., 3])
        filled += int(hole.sum())
    return filled


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
