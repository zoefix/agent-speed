"""Precise hand masks with Segment Anything (ViT-B), prompted by the pose keypoints.

See-Through's hand layers are soft blobs that take in the book between the fingers and the cloth around them, so a
hand cut from them drags those along when its fingers move. SAM sees the outline of the hand instead. Prompts: a box
around the hand, positive points along every finger and on the palm, negative points around the hand away from it. Of all
the masks SAM offers for those prompts, the smallest one that still holds the palm and the fingers wins: the larger
ones take in cloth of the same color next to the hand.
"""
from functools import lru_cache

import numpy as np
from scipy import ndimage

from . import hands


@lru_cache(maxsize=1)
def _predictor(checkpoint):
    import torch
    from segment_anything import SamPredictor, sam_model_registry
    sam = sam_model_registry["vit_b"](checkpoint=checkpoint)
    sam.to("mps" if torch.backends.mps.is_available() else "cpu")
    return SamPredictor(sam)


def _bone_distance(points, hand):
    segs = [(hand["wrist"], f[0]) for f in hand["fingers"]]
    segs += [(f[j], f[j + 1]) for f in hand["fingers"] for j in range(3)]
    d = np.full(len(points), np.inf)
    for a, b in segs:
        ab = b - a
        t = np.clip(((points - a) @ ab) / max(float(ab @ ab), 1e-9), 0, 1)
        d = np.minimum(d, np.linalg.norm(points - (a + t[:, None] * ab), axis=1))
    return d


def hand_mask(reference, hand, checkpoint):
    """Boolean canvas mask of the hand, or None when SAM finds nothing that fits the keypoints."""
    h, w = reference.shape[:2]
    size = hand["size"]
    pts = np.concatenate([[hand["wrist"]]] + list(hand["fingers"]))
    x0, y0 = np.maximum(np.floor(pts.min(axis=0) - 0.45 * size), 0).astype(int)
    x1, y1 = np.minimum(np.ceil(pts.max(axis=0) + 0.45 * size), [w, h]).astype(int)
    crop = (np.clip(reference[y0:y1, x0:x1], 0, 1) * 255).astype(np.uint8)
    origin = np.array([x0, y0], np.float64)

    palm = np.array([hand["palm"]] + [0.5 * (hand["wrist"] + f[0]) for f in hand["fingers"][1:]]
                    + [0.5 * (hand["palm"] + f[0]) for f in hand["fingers"][1:]])
    tips = np.array([q for f in hand["fingers"] for q in (f[1], 0.5 * (f[1] + f[2]), f[2], 0.5 * (f[2] + f[3]))])
    positive = np.concatenate([palm, tips])
    ring = []
    for a in np.linspace(0, 2 * np.pi, 16, endpoint=False):
        for r in (0.6, 0.8):
            ring.append(hand["palm"] + r * size * np.array([np.cos(a), np.sin(a)]))
    ring = np.array(ring)
    ring = ring[(_bone_distance(ring, hand) > 0.32 * size) & np.all((ring >= [x0, y0]) & (ring < [x1, y1]), axis=1)]

    predictor = _predictor(str(checkpoint))
    predictor.set_image(crop)
    box = np.concatenate([pts.min(axis=0) - 0.12 * size, pts.max(axis=0) + 0.12 * size]) - np.tile(origin, 2)
    prompts = [dict(box=box), dict(box=box, point_coords=positive - origin, point_labels=np.ones(len(positive))),
               dict(box=box, point_coords=np.concatenate([positive, ring]) - origin,
                    point_labels=np.concatenate([np.ones(len(positive)), np.zeros(len(ring))]))]

    def inside(m, p):
        q = np.clip(np.round(p - origin).astype(int), 0, [m.shape[1] - 1, m.shape[0] - 1])
        return m[q[:, 1], q[:, 0]].mean() if len(q) else 0.0

    candidates = []
    for prompt in prompts:
        masks, scores, _ = predictor.predict(multimask_output=True, **prompt)
        candidates += [(m, float(score), inside(m, palm), inside(m, tips), inside(m, ring))
                       for m, score in zip(masks, scores)]
    reach = float((pts.max(axis=0) - pts.min(axis=0)).max())
    clean = [c for c in candidates if c[1] >= 0.8 and c[4] <= 0.1 and 0.05 * size * size < c[0].sum() < 2.0 * reach * reach]
    fitting = [c for c in clean if min(c[2], c[3]) >= 0.75]
    if fitting:
        best = min(fitting, key=lambda c: c[0].sum())[0]
    else:
        # An object across the hand (a flute over the knuckles or the fingers) leaves part of it outside every mask;
        # parted fingertips are asked for below.
        parted = [c for c in clean if max(c[2], c[3]) >= 0.75 and min(c[2], c[3]) >= 0.5]
        if not parted:
            return None
        best = max(parted, key=lambda c: (round(c[2] + c[3], 2), -c[0].sum()))[0]
    best = ndimage.binary_opening(ndimage.binary_fill_holes(best), iterations=1)
    labels, count = ndimage.label(best)
    if count > 1:
        sizes = ndimage.sum(best, labels, range(1, count + 1))
        best = labels == (int(np.argmax(sizes)) + 1)
    best |= _parted_fingers(predictor, best, hand, origin, ring, inside, crop)
    best &= ~hands.held(hands.off_hue(crop / 255, best, best), hand["size"])
    out = np.zeros((h, w), bool)
    out[y0:y1, x0:x1] = best
    return out


def _parted_fingers(predictor, best, hand, origin, ring, inside, crop, near_color=30.0, touch=0.05):
    """The fingertips an object across the hand (a flute) parts from the palm: SAM is asked for them on their own.
    The answer counts only when it is truly parted (at most `touch` of its rim on the palm) and has the hand's color
    (a book's spine behind curled fingers does not)."""
    size = hand["size"]
    near = ndimage.binary_dilation(best, iterations=3)
    parted = [f for f in hand["fingers"] if inside(near, f[3][None]) < 0.5]
    if not parted:
        return np.zeros_like(best)
    tips = np.array([q for f in parted for q in (f[2], 0.5 * (f[2] + f[3]), f[3])])
    box = np.concatenate([tips.min(axis=0) - 0.12 * size, tips.max(axis=0) + 0.12 * size]) - np.tile(origin, 2)
    masks, scores, _ = predictor.predict(box=box, point_coords=np.concatenate([tips, ring]) - origin,
                                         point_labels=np.concatenate([np.ones(len(tips)), np.zeros(len(ring))]),
                                         multimask_output=True)
    fitting = [m for m, score in zip(masks, scores) if score >= 0.7 and inside(m, tips) >= 0.6
               and inside(m, ring) <= 0.1 and m.sum() < 0.6 * size * size]
    if not fitting:
        return np.zeros_like(best)
    tips_mask = min(fitting, key=lambda m: m.sum()) & ~best
    if not tips_mask.any():
        return np.zeros_like(best)
    rim = ndimage.binary_dilation(tips_mask, iterations=2) & ~tips_mask
    rgb = crop.astype(np.float32)
    color = np.linalg.norm(np.median(rgb[tips_mask], axis=0) - np.median(rgb[best], axis=0))
    parted = (rim & best).sum() <= touch * rim.sum()
    return tips_mask if parted and color <= near_color else np.zeros_like(best)


def prop_mask(reference, points, checkpoint, min_score=0.8, hole_share=0.05):
    """Boolean canvas mask of a held object marked by `points` (canvas pixels) on it, such as a doll See-Through takes
    for a second head and spreads over the face, hair, eye and arm layers. Each point asks SAM on its own, and the
    smallest mask it scores at least `min_score` (the face of the doll, or its hair) joins the object; enclosed gaps
    under `hole_share` of it (eyes, mouth) are filled, larger ones (a hand across it) stay out. Negative prompts on
    the hands break SAM's masks into specks, so a hand pressed to the object can come along with it."""
    predictor = _predictor(str(checkpoint))
    predictor.set_image((np.clip(reference, 0, 1) * 255).astype(np.uint8))
    mask = np.zeros(reference.shape[:2], bool)
    for point in points:
        masks, scores, _ = predictor.predict(point_coords=np.array([point], np.float64), point_labels=np.ones(1),
                                             multimask_output=True)
        sure = [m for m, score in zip(masks, scores) if score >= min_score]
        mask |= min(sure, key=lambda m: m.sum()) if sure else masks[int(np.argmax(scores))]
    mask = ndimage.binary_closing(mask, iterations=3)
    holes = ndimage.binary_fill_holes(mask) & ~mask
    labels, count = ndimage.label(holes)
    if count:
        sizes = ndimage.sum(holes, labels, range(1, count + 1))
        mask |= np.isin(labels, 1 + np.flatnonzero(sizes < hole_share * mask.sum()))
    return mask


def tail_mask(reference, alpha, checkpoint, points=5, min_score=0.8, max_ratio=2.5, cover=0.9):
    """The whole tail in the art, prompted by `points` on the tail layer (`alpha`, at its thickest): the largest mask
    SAM scores at least `min_score` that holds `cover` of the tail layer and is at most `max_ratio` times its size,
    or None. It finds the part of the tail the split drew into the hair."""
    solid = alpha > 0.5
    if not solid.any():
        return None
    depth = ndimage.distance_transform_edt(solid)
    ys, xs = np.nonzero(depth > 0.6 * depth.max())
    pick = np.linspace(0, len(ys) - 1, points).astype(int)
    coords = np.array([(xs[i], ys[i]) for i in pick], np.float64)
    predictor = _predictor(str(checkpoint))
    predictor.set_image((np.clip(reference, 0, 1) * 255).astype(np.uint8))
    masks, scores, _ = predictor.predict(point_coords=coords, point_labels=np.ones(len(coords)), multimask_output=True)
    area = solid.sum()
    fits = [m for m, score in zip(masks, scores) if score >= min_score and (m & solid).sum() >= cover * area
            and m.sum() <= max_ratio * area]
    return max(fits, key=lambda m: m.sum()) if fits else None

