"""Arm and hand joints from a whole-body pose estimator (RTMW via rtmlib), mapped onto the square character canvas.

COCO-WholeBody order: shoulders 5/6, elbows 7/8, wrists 9/10, hips 11/12; the person's left hand is 91-111, the right 112-132,
each wrist, then thumb, index, middle, ring and pinky with four points from the base to the tip.
"""
from functools import lru_cache

import cv2
import numpy as np

SHOULDERS, ELBOWS, WRISTS, HIPS = (5, 6), (7, 8), (9, 10), (11, 12)
HANDS = (91, 112)
MIN_SCORE = 1.5
MIN_HAND_SCORE = 2.0
# Traced wrist to body wrist, and wrist to middle fingertip, as shares of the whole-body hand's extent.
MAX_WRIST_STRAY = 0.5
MIN_HAND_REACH = 0.15
HAND_MODEL = ("https://download.openmmlab.com/mmpose/v1/projects/rtmposev1/onnx_sdk/"
              "rtmpose-m_simcc-hand5_pt-aic-coco_210e-256x256-74fb594_20230320.zip")


@lru_cache(maxsize=1)
def _model():
    from rtmlib import Wholebody
    return Wholebody(mode="performance", backend="onnxruntime", device="cpu")


@lru_cache(maxsize=1)
def _hand_model():
    from rtmlib import RTMPose
    return RTMPose(HAND_MODEL, model_input_size=(256, 256), backend="onnxruntime", device="cpu")


@lru_cache(maxsize=8)
def _refined_hands(image_path):
    """The whole-body model places fingers loosely; a hand model run on each hand's crop traces them closely. A hand
    whose traced wrist strays from the body's wrist, or that collapses to a point, traced something else (a face, a
    staff) and is dropped, so that arm keeps its hand."""
    found = _detect(image_path)
    if found is None:
        return {}
    k, s, _ = found
    img = cv2.imread(image_path)
    out = {}
    for base, wrist in zip(HANDS, WRISTS):
        if float(s[base:base + 21].mean()) < MIN_HAND_SCORE:
            continue
        pts = k[base:base + 21]
        (x0, y0), (x1, y1) = pts.min(axis=0), pts.max(axis=0)
        extent = max(x1 - x0, y1 - y0)
        pad = 0.25 * extent
        refined, _ = _hand_model()(img, bboxes=[[x0 - pad, y0 - pad, x1 + pad, y1 + pad]])
        hand = refined[0]
        strays = np.linalg.norm(hand[0] - k[wrist]) > MAX_WRIST_STRAY * extent
        collapsed = np.linalg.norm(hand[12] - hand[0]) < MIN_HAND_REACH * extent
        if not (strays or collapsed):
            out[base] = hand
    return out


@lru_cache(maxsize=8)
def _detect(image_path):
    img = cv2.imread(str(image_path))
    if img is None:
        return None
    kps, scores = _model()(img)
    if len(kps) == 0:
        return None
    best = int(np.argmax([s[list(SHOULDERS + ELBOWS + WRISTS)].mean() for s in scores]))
    return kps[best], scores[best], img.shape[1::-1]


def canvas_transform(image_size, size):
    """2x3 matrix from image pixels to the canvas that layers.load_reference builds (centered square, resized)."""
    w, h = image_size
    side = max(w, h)
    k = size[0] / side
    return np.array([[k, 0, (side - w) / 2 * k], [0, k, (side - h) / 2 * k]], np.float64)


def _to_canvas(image_size, size, matrix):
    to_canvas = canvas_transform(image_size, size)
    if matrix is not None:
        to_canvas = (np.vstack([matrix, [0, 0, 1]]) @ np.vstack([to_canvas, [0, 0, 1]]))[:2]
    return to_canvas


def arms(image_path, size, center_x, matrix=None):
    """{"left"|"right": {"shoulder", "elbow", "wrist", "score"}} by image side, or {} when nothing is found."""
    found = _detect(str(image_path))
    if found is None:
        return {}
    k, s, image_size = found
    to_canvas = _to_canvas(image_size, size, matrix)
    out = {}
    for sh, el, wr in zip(SHOULDERS, ELBOWS, WRISTS):
        score = float(min(s[sh], s[el], s[wr]))
        if score < MIN_SCORE:
            continue
        pts = cv2.transform(np.float64([[k[sh], k[el], k[wr]]]), to_canvas)[0]
        side = _side(pts[0], pts[1], center_x)
        out[side] = {"shoulder": tuple(pts[0]), "elbow": tuple(pts[1]), "wrist": tuple(pts[2]), "score": score}
    return out


def torso(image_path, size, matrix=None):
    """{"shoulder": y, "hip": y, "length": px} of the shoulder and hip midpoints on the canvas (length between them),
    or None unless all four are found."""
    found = _detect(str(image_path))
    if found is None:
        return None
    k, s, image_size = found
    if min(s[i] for i in SHOULDERS + HIPS) < MIN_SCORE:
        return None
    pts = cv2.transform(np.float64([[k[i] for i in SHOULDERS + HIPS]]), _to_canvas(image_size, size, None))[0]
    shoulder, hip = pts[:2].mean(axis=0), pts[2:].mean(axis=0)
    return {"shoulder": float(shoulder[1]), "hip": float(hip[1]), "length": float(np.linalg.norm(shoulder - hip))}


def hands(image_path, size, center_x, matrix=None):
    """{"left"|"right": 21 canvas points [wrist, thumb x4, index x4, middle x4, ring x4, pinky x4]} by the image side
    of the hand's upper arm, so a hand reaching across the body (both hands on a flute) stays with its own arm."""
    found = _detect(str(image_path))
    if found is None:
        return {}
    k, _, image_size = found
    to_canvas = _to_canvas(image_size, size, matrix)
    out = {}
    for base, points in _refined_hands(str(image_path)).items():
        pts = cv2.transform(np.float64([points]), to_canvas)[0]
        arm = HANDS.index(base)
        shoulder, elbow = cv2.transform(np.float64([[k[SHOULDERS[arm]], k[ELBOWS[arm]]]]), to_canvas)[0]
        out[_side(shoulder, elbow, center_x)] = pts
    return out


def _side(shoulder, elbow, center_x):
    """Image side of an arm by its upper arm, which rarely crosses the body's middle (a hand often does, and a
    raised arm's shoulder can be placed on the neck)."""
    return "left" if 0.5 * (shoulder[0] + elbow[0]) < center_x else "right"
