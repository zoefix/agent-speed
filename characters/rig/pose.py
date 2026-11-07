"""Arm joints from a whole-body pose estimator (RTMW via rtmlib), mapped onto the square character canvas."""
from functools import lru_cache

import cv2
import numpy as np

SHOULDERS, ELBOWS, WRISTS = (5, 6), (7, 8), (9, 10)
MIN_SCORE = 1.5


@lru_cache(maxsize=1)
def _model():
    from rtmlib import Wholebody
    return Wholebody(mode="performance", backend="onnxruntime", device="cpu")


def canvas_transform(image_size, size):
    """2x3 matrix from image pixels to the canvas that layers.load_reference builds (centered square, resized)."""
    w, h = image_size
    side = max(w, h)
    k = size[0] / side
    return np.array([[k, 0, (side - w) / 2 * k], [0, k, (side - h) / 2 * k]], np.float64)


def arms(image_path, size, center_x, matrix=None):
    """{"left"|"right": {"shoulder", "elbow", "wrist", "score"}} by image side, or {} when nothing is found."""
    img = cv2.imread(str(image_path))
    if img is None:
        return {}
    kps, scores = _model()(img)
    if len(kps) == 0:
        return {}
    best = int(np.argmax([s[list(SHOULDERS + ELBOWS + WRISTS)].mean() for s in scores]))
    k, s = kps[best], scores[best]
    to_canvas = canvas_transform(img.shape[1::-1], size)
    if matrix is not None:
        to_canvas = (np.vstack([matrix, [0, 0, 1]]) @ np.vstack([to_canvas, [0, 0, 1]]))[:2]
    out = {}
    for sh, el, wr in zip(SHOULDERS, ELBOWS, WRISTS):
        score = float(min(s[sh], s[el], s[wr]))
        if score < MIN_SCORE:
            continue
        pts = cv2.transform(np.float64([[k[sh], k[el], k[wr]]]), to_canvas)[0]
        side = "left" if pts[0][0] < center_x else "right"
        out[side] = {"shoulder": tuple(pts[0]), "elbow": tuple(pts[1]), "wrist": tuple(pts[2]), "score": score}
    return out
