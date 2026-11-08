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
        candidates += [(m, float(score), min(inside(m, palm), inside(m, tips)), inside(m, ring))
                       for m, score in zip(masks, scores)]
    reach = float((pts.max(axis=0) - pts.min(axis=0)).max())
    fitting = [c for c in candidates if c[1] >= 0.8 and c[2] >= 0.75 and c[3] <= 0.1
               and 0.05 * size * size < c[0].sum() < 2.0 * reach * reach]
    if not fitting:
        return None
    best = min(fitting, key=lambda c: c[0].sum())[0]
    best = ndimage.binary_opening(ndimage.binary_fill_holes(best), iterations=1)
    labels, count = ndimage.label(best)
    if count > 1:
        sizes = ndimage.sum(best, labels, range(1, count + 1))
        best = labels == (int(np.argmax(sizes)) + 1)
    out = np.zeros((h, w), bool)
    out[y0:y1, x0:x1] = best
    return out
