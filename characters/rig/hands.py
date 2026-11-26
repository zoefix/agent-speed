"""Hands: a wrist bone and three bones per finger from pose keypoints, the hand cut out of its arm layer as a part with
a fine mesh, and skinning weights by distance to the bone segments.

Fingers curl in the picture plane toward the palm, and their outer segments slide toward the knuckles, which reads as
the finger bending toward or away from the viewer.
"""
import numpy as np
from scipy import ndimage

from .layers import Layer

# Creases between curled fingers up to twice this many px wide close into the hand (split), and so do enclosed white
# pinholes up to (PINHOLE x the hand's size) squared px (at least PINHOLE_MIN).
CREASE = 2
PINHOLE, PINHOLE_MIN = 0.1, 12
FINGERS = ("thumb", "index", "middle", "ring", "pinky")
CURL = (14.0, 20.0, 16.0)
FIST = 1.35
SHORTEN = (0.0, 0.18, 0.22)
SPREAD = (10.0, 6.0, 0.0, 5.0, 9.0)


def _cross(a, b):
    return a[0] * b[1] - a[1] * b[0]


def _angle(v):
    return np.arctan2(v[1], v[0])


def landmarks(points, elbow):
    """Hand landmarks from 21 canvas points; `elbow` fixes which way the hand points."""
    p = np.asarray(points, np.float64)
    wrist = p[0]
    fingers = [p[1 + 4 * i:5 + 4 * i] for i in range(5)]
    palm = np.mean([wrist] + [f[0] for f in fingers[1:]], axis=0)
    size = float(np.linalg.norm(fingers[2][3] - wrist))
    mid = fingers[2][3] - fingers[2][0]
    curl, spread = [], []
    for i, f in enumerate(fingers):
        d = f[3] - f[0]
        curl.append(1.0 if _cross(d, palm - f[0]) > 0 else -1.0)
        delta = (_angle(d) - _angle(mid) + np.pi) % (2 * np.pi) - np.pi
        spread.append(0.0 if i == 2 else float(np.sign(delta) or 1.0))
    reach = np.mean([np.linalg.norm(f[3] - wrist) for f in fingers[1:]])
    knuckles = np.mean([np.linalg.norm(f[0] - wrist) for f in fingers[1:]])
    return {"wrist": wrist, "elbow": np.asarray(elbow, np.float64), "palm": palm, "fingers": fingers, "size": size,
            "curl": curl, "spread": spread, "open": float(reach / max(knuckles, 1e-6))}


def _segments(hand):
    """(bone, start, end) for the forearm, the palm and every finger segment; the last one runs past the tip."""
    side = hand["side"]
    segs = [(f"forearm.{side}", hand["elbow"], hand["wrist"])]
    for f in hand["fingers"]:
        segs.append((f"hand.{side}", hand["wrist"], f[0]))
    for i, f in enumerate(hand["fingers"]):
        for j in range(3):
            end = f[j + 1] if j < 2 else f[3] + 0.35 * (f[3] - f[2])
            segs.append((f"finger.{side}.{i}.{j}", f[j], end))
    return segs


def _distance(points, a, b):
    ab = b - a
    t = np.clip(((points - a) @ ab) / max(float(ab @ ab), 1e-9), 0, 1)
    return np.linalg.norm(points - (a + t[:, None] * ab), axis=1)


def _beyond_wrist(points, hand):
    u = hand["palm"] - hand["wrist"]
    u = u / max(np.linalg.norm(u), 1e-9)
    return (points - hand["wrist"]) @ u


def _capsules(shape, hand):
    h, w = shape
    size = hand["size"]
    allp = np.concatenate([[hand["wrist"]]] + list(hand["fingers"]))
    x0, y0 = np.maximum(np.floor(allp.min(axis=0) - 0.6 * size), 0).astype(int)
    x1, y1 = np.minimum(np.ceil(allp.max(axis=0) + 0.6 * size), [w, h]).astype(int)
    ys, xs = np.mgrid[y0:y1, x0:x1]
    pts = np.stack([xs.ravel(), ys.ravel()], 1).astype(np.float64)
    near = np.full(len(pts), np.inf)
    for bone, a, b in _segments(hand)[1:]:
        near = np.minimum(near, _distance(pts, a, b))
    out = np.zeros((h, w), bool)
    out[y0:y1, x0:x1] = (near < 0.3 * size).reshape(y1 - y0, x1 - x0)
    return out


def past_cut(shape, hand):
    h, w = shape
    ys, xs = np.mgrid[0:h, 0:w]
    u = hand["palm"] - hand["wrist"]
    u = u / max(np.linalg.norm(u), 1e-9)
    return (xs - hand["wrist"][0]) * u[0] + (ys - hand["wrist"][1]) * u[1] > -0.12 * hand["size"]


def split(layer, hand, mask=None, reference=None, holds=True):
    """(arm layer, hand layer). With a precise `mask` and the original art the hand part is exactly the drawn hand
    plus the ink line around it (SAM stops inside the outline), keeping the arm layer's own colors and soft edge where
    it has them and never the paper behind the art; without, it is the arm layer near the hand bones. The cut sits just before the wrist and the hand part reaches
    4 px back over the arm, so no seam opens between them. A hand that `holds` nothing (no object layer touches it)
    keeps all it encloses: the dark hollow of an empty fist is not a held object."""
    shape = layer.alpha.shape
    past = past_cut(shape, hand)
    held_area = np.zeros(shape, bool)
    if mask is not None and reference is not None:
        ink = reference @ np.array([0.299, 0.587, 0.114], np.float32) < 0.6
        outline = ndimage.binary_dilation(mask, iterations=4) & ink
        # A crease between curled fingers open to the outside by a thin channel is the hand's too: left on the arm,
        # it stayed behind as slivers and teal pinholes once the wrist turned (paper in it is taken out below).
        drawn = mask | outline
        inside = ndimage.binary_fill_holes(drawn | ndimage.binary_closing(drawn, iterations=CREASE)) & past
        # Filling the outline must not take in a held object (a flute between parted fingers and the palm, or across
        # the fingers): thick areas outside SAM's hand or off the hand's hue stay with the layer that drew them.
        if holds:
            held_area = held(inside & ~mask | off_hue(reference, inside, mask), hand["size"])
        paper = (reference.min(axis=2) > 0.975) & (layer.alpha < 0.5)
        inside = (inside | (ndimage.binary_dilation(inside & ~held_area, iterations=2) & past & (layer.alpha > 0.01)))
        inside &= ~paper & ~held_area
        # A pinhole of white the hand encloses (a highlight in a fist's crease the matte took for paper) is the hand's:
        # left out, it showed whatever lay behind once the wrist turned, teal at worst.
        pockets, count = ndimage.label(ndimage.binary_fill_holes(inside) & ~inside & ~held_area)
        if count:
            sizes = np.asarray(ndimage.sum(pockets > 0, pockets, range(1, count + 1)))
            small = 1 + np.flatnonzero(sizes <= max(PINHOLE_MIN, (PINHOLE * hand["size"]) ** 2))
            inside |= np.isin(pockets, small)
        rim = inside & ndimage.binary_dilation(reference.min(axis=2) > 0.975, iterations=1) & (layer.alpha > 0.05)
        part = np.zeros_like(layer.rgba)
        part[..., :3] = np.where(rim[..., None], layer.rgba[..., :3], reference)
        soft = np.maximum(ndimage.gaussian_filter(inside.astype(np.float32), 0.6), 0.5)
        # Only the outline against the paper is soft; where the arm goes on (the cuff), the arm is cut away under the
        # hand, so a soft edge there would let the desktop through.
        soft = np.where(ndimage.binary_dilation((layer.alpha > 0.5) & ~inside, iterations=2), 1.0, soft)
        part[..., 3] = np.where(inside, np.where(rim, layer.alpha, soft), 0.0)
    else:
        inside = _capsules(shape, hand) & past & (layer.alpha > 0.01)
        part = layer.rgba.copy()
        part[..., 3] = np.where(inside, part[..., 3], 0.0)
    # The hand reaches 4 px over the arm wherever they meet (the cut and a cuff drawn over the wrist), so the
    # mipmapped edge of the hand never shows through where the arm is cut away.
    band = ndimage.binary_dilation(inside, iterations=4) & ~inside & ~held_area & (layer.alpha > 0.01)
    part[..., :3] = np.where(band[..., None], layer.rgba[..., :3], part[..., :3])
    part[..., 3] = np.where(band, layer.alpha, part[..., 3])
    arm = layer.rgba.copy()
    arm[..., 3] = np.where(inside, 0.0, arm[..., 3])
    _line_cuff(arm, inside, hand["size"])
    return Layer(layer.name, layer.tag, arm, layer.side), Layer(f"hand.{hand['side']}", "hand", part, hand["side"])


def _line_cuff(arm, inside, size, reach=0.3):
    """Fills what the hand covered of its own sleeve's opening (within the convex hull of the sleeve's main piece up
    to `reach` of the hand's size from the hand) with the sleeve's colors around it, opaque: once the hand turns or
    sways, the cuff shows its lining instead of a hand-shaped hole to the stage. The hand covers it at rest."""
    import cv2
    from skimage.morphology import convex_hull_image
    kept = arm[..., 3] > 0.5
    if not (kept.any() and inside.any()):
        return
    r = max(3, int(reach * size))
    ys, xs = np.nonzero(inside)
    y0, y1 = max(ys.min() - 2 * r, 0), ys.max() + 2 * r + 1
    x0, x1 = max(xs.min() - 2 * r, 0), xs.max() + 2 * r + 1
    box = (slice(y0, y1), slice(x0, x1))
    labels, count = ndimage.label(kept[box])
    if not count:
        return
    main = labels == 1 + int(np.argmax(ndimage.sum(kept[box], labels, range(1, count + 1))))
    near = main & ndimage.binary_dilation(inside[box], iterations=r)
    if near.sum() < 3:
        return
    lining = convex_hull_image(near) & inside[box] & ~kept[box]
    if not lining.any():
        return
    rgb = (np.clip(arm[box][..., :3], 0, 1) * 255).astype(np.uint8)
    rgb[~kept[box]] = 0
    fixed = cv2.inpaint(rgb, (~kept[box]).astype(np.uint8), 5, cv2.INPAINT_TELEA).astype(np.float32) / 255
    view = arm[box]
    view[..., :3] = np.where(lining[..., None], fixed, view[..., :3])
    view[..., 3] = np.where(lining, 1.0, view[..., 3])


def _chroma(rgb):
    rgb = np.asarray(rgb, np.float32)
    return np.stack([rgb[..., 0] - rgb[..., 1], 0.5 * (rgb[..., 0] + rgb[..., 1]) - rgb[..., 2]], axis=-1)


def off_hue(rgb, where, hand_mask, far=0.1):
    """Pixels of `where` whose hue (red-green, yellow-blue) is far from the hand's median over `hand_mask`. Hue alone,
    so shading on the skin never counts and a glove the color of the object keeps everything."""
    chroma = _chroma(rgb)
    if not hand_mask.any():
        return np.zeros(where.shape, bool)
    return where & (np.linalg.norm(chroma - np.median(chroma[hand_mask], axis=0), axis=-1) > far)


def held(candidates, size, share=0.03):
    """The thick parts of `candidates`: at least `share` of the hand's size squared once thin lines are opened away,
    so ink between the fingers and a painted nail stay with the hand."""
    thick = ndimage.binary_opening(candidates, iterations=2)
    labels, count = ndimage.label(thick)
    if count == 0:
        return np.zeros(candidates.shape, bool)
    sizes = ndimage.sum(thick, labels, range(1, count + 1))
    big = np.isin(labels, 1 + np.flatnonzero(sizes >= share * size * size))
    return ndimage.binary_dilation(big, iterations=2) & candidates


def clear_ghosts(parts, mask, keep):
    """Other layers that took the hand's colors at reprojection give them up: a layer drawn above the hand (hair the
    hand was in front of, the other sleeve drawn over this arm) turns transparent there, a layer below gets the hand
    inpainted away, so a moving finger never leaves a copy of itself behind and nothing covers it. The hand is drawn
    right above its arm `keep`, so that decides which layers lie above it (by tag without one)."""
    import cv2
    from .layers import DRAW_ORDER, owner_map
    owner = owner_map(parts)
    above = {tag for tag in DRAW_ORDER[DRAW_ORDER.index("hand") + 1:]}
    arm = next((i for i, layer in enumerate(parts) if layer.name == keep), None)
    ys, xs = np.nonzero(mask)
    if len(ys) == 0:
        return 0
    y0, y1 = max(ys.min() - 12, 0), ys.max() + 13
    x0, x1 = max(xs.min() - 12, 0), xs.max() + 13
    cleared = 0
    for i, layer in enumerate(parts):
        if layer.name == keep:
            continue
        ghost = (mask & (owner == i))[y0:y1, x0:x1]
        if not ghost.any():
            continue
        if (i > arm) if arm is not None else layer.tag in above:
            layer.rgba[y0:y1, x0:x1, 3] = np.where(ghost, 0.0, layer.rgba[y0:y1, x0:x1, 3])
            cleared += int(ghost.sum())
            continue
        rgb = (np.clip(layer.rgba[y0:y1, x0:x1, :3], 0, 1) * 255).astype(np.uint8)
        fixed = cv2.inpaint(rgb, ndimage.binary_dilation(ghost, iterations=1).astype(np.uint8), 6, cv2.INPAINT_TELEA)
        layer.rgba[y0:y1, x0:x1, :3] = np.where(ghost[..., None], fixed.astype(np.float32) / 255,
                                                 layer.rgba[y0:y1, x0:x1, :3])
        cleared += int(ghost.sum())
    return cleared


INK = 0.45
# Layers that draw over the hand what it touches on the head: the front hair, a headphone's cup (which the split may
# also call ears), a hat's brim.
OVER_HAND = ("front hair", "earwear", "headwear", "ears")


def _small(mask, limit):
    """The 8-connected pieces of `mask` under `limit` px."""
    labels, count = ndimage.label(mask, structure=np.ones((3, 3)))
    if not count:
        return np.zeros(mask.shape, bool)
    sizes = np.asarray(ndimage.sum(mask, labels, range(1, count + 1)))
    return np.isin(labels, 1 + np.flatnonzero(sizes < limit))


def bits_in_hair(parts, hand, mask, reach=8, piece=0.1, far=0.2, tone=None, over=OVER_HAND):
    """Bits of the hand that the front hair took (knuckles seen between strands crossing the hand, or fingertips on a
    headphone's cup: any OVER_HAND layer): front hair pixels
    within `reach` px of the hand's mask whose hue is nearer the hand's than the hair's around them (and within `far`
    of the hand's, as off_hue) and their dark outline next to them (under INK luminance, nearer the hand's ink than
    the hair's), in pieces under `piece` of the mask's area, then a fist's redder creases touching those pieces or the
    hand (within twice `far` of its hue and less than half as far as the hair's), go to the hand part, composited as
    they showed. Layers between the two give those pixels up where the hair hid them at rest. A gesture replacing the
    hand then takes them along instead of leaving them in the hair, and a sway carries them. Returns the pixels moved. For an arm whose
    hand has no part of its own (the pose model missed it), `hand` is the arm layer and `tone` the skin's hue."""
    i_hand = next((i for i, p in enumerate(parts) if p is hand), None)
    if i_hand is None or not mask.any():
        return 0
    near = ndimage.binary_dilation(mask, iterations=reach)
    ring = ndimage.binary_dilation(near, iterations=3 * reach) & ~near
    skin = (hand.alpha > 0.9) & mask
    ink = None
    if tone is None:
        if skin.sum() < 50:
            return 0
        tone = np.median(_chroma(hand.rgba[..., :3])[skin], axis=0)
        luma = hand.rgba[..., :3] @ np.array([0.299, 0.587, 0.114], np.float32)
        lines = (hand.alpha > 0.5) & ndimage.binary_dilation(mask, iterations=2) & (luma < INK)
        ink = np.median(_chroma(hand.rgba[..., :3])[lines], axis=0) if lines.sum() >= 20 else None
    moved = 0
    for i, hair in enumerate(parts):
        if hair.tag not in over or i < i_hand:
            continue
        around = (hair.alpha > 0.9) & ring
        if around.sum() < 50:
            continue
        chroma = _chroma(hair.rgba[..., :3])
        own = np.median(chroma[around], axis=0)
        to_hand = np.linalg.norm(chroma - tone, axis=-1)
        between = parts[i_hand + 1:i]
        hidden = hair.alpha > 0.99
        clear = np.all([(p.alpha < 0.01) | hidden for p in between], axis=0) if between else np.ones(mask.shape, bool)
        to_own = np.linalg.norm(chroma - own, axis=-1)
        alike = near & (hair.alpha > 0.3) & clear & (to_hand <= far) & (to_hand < to_own)
        dark = (hair.rgba[..., :3] @ np.array([0.299, 0.587, 0.114], np.float32)) < INK
        own_ink = (hair.alpha > 0.9) & ring & dark
        if ink is not None and own_ink.sum() >= 20:
            # The hand's outline the hair drew too: dark, and nearer the hand's ink than the hair's.
            to_ink = np.linalg.norm(chroma - ink, axis=-1)
            outline = near & (hair.alpha > 0.3) & clear & dark
            outline &= to_ink < np.linalg.norm(chroma - np.median(chroma[own_ink], axis=0), axis=-1)
            alike |= outline & ndimage.binary_dilation(alike | mask, iterations=3)
        take = _small(alike, piece * mask.sum())
        # A fist's creases are redder than its skin: past `far`, up to twice it, when the hair's hue is twice as far,
        # in pieces touching what goes or the hand itself (they joined the skin bits into one piece too big before).
        creases = near & (hair.alpha > 0.3) & clear & ~take & (to_hand <= 2 * far) & (2 * to_hand < to_own)
        labels, count = ndimage.label(creases, structure=np.ones((3, 3)))
        if count:
            touching = np.unique(labels[ndimage.binary_dilation(take | mask, iterations=2) & creases])
            take |= _small(np.isin(labels, touching[touching > 0]), piece * mask.sum())
        if not take.any():
            continue
        a, t = hair.alpha[take][:, None], hand.alpha[take][:, None]
        total = a + t * (1 - a)
        color = (hair.rgba[take][:, :3] * a + hand.rgba[take][:, :3] * t * (1 - a)) / np.maximum(total, 1e-6)
        hand.rgba[take] = np.concatenate([color, total], axis=1)
        hair.rgba[..., 3][take] = 0.0
        for p in between:
            p.rgba[..., 3][take] = 0.0
        moved += int(take.sum())
    return moved


def weights(vertices, hand):
    """Soft nearest-segment weights; near the cut the hand follows the forearm only, so the seam never opens."""
    v = np.asarray(vertices, np.float64)
    segs = _segments(hand)
    d = np.stack([_distance(v, a, b) for _, a, b in segs], 1)
    soft = 0.06 * hand["size"]
    w = np.exp(-(d - d.min(axis=1, keepdims=True)) / max(soft, 1e-6))
    w /= w.sum(axis=1, keepdims=True)
    t = _beyond_wrist(v, hand) / max(hand["size"], 1e-9)
    blend = np.clip((t + 0.06) / 0.16, 0, 1)
    blend = blend * blend * (3 - 2 * blend)
    acc = {}
    for k, (bone, _, _) in enumerate(segs):
        acc.setdefault(bone, np.zeros(len(v)))
        acc[bone] += w[:, k] * blend
    fore = f"forearm.{hand['side']}"
    acc[fore] = acc.get(fore, np.zeros(len(v))) + (1 - blend)
    return list(acc.items())


def bones(hand, bone_cls):
    side = hand["side"]
    out = [bone_cls(f"hand.{side}", f"forearm.{side}", tuple(hand["wrist"]))]
    for i, f in enumerate(hand["fingers"]):
        parent = f"hand.{side}"
        for j in range(3):
            name = f"finger.{side}.{i}.{j}"
            out.append(bone_cls(name, parent, tuple(f[j])))
            parent = name
    return out


def summary(hand):
    return {"wrist": hand["wrist"].tolist(), "size": hand["size"], "open": hand["open"],
            "fingers": [f.tolist() for f in hand["fingers"]], "curl": hand["curl"], "spread": hand["spread"]}
