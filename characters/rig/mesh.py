"""Triangle meshes that cover each layer's visible pixels."""
import cv2
import numpy as np
from scipy import ndimage
from scipy.spatial import Delaunay

FINE = {"eyewhite", "irides", "eyelash", "eyebrow", "nose", "mouth", "earwear", "ears", "eyewear", "hand"}
MEDIUM = {"face", "headwear", "neck", "neckwear", "objects"}


def spacing_for(tag, area):
    if tag in FINE:
        return 5
    if tag in MEDIUM:
        return 14
    return int(np.clip(np.sqrt(area) / 12, 12, 26))


def build_mesh(alpha, tag, threshold=0.02, pad=2):
    mask = alpha > threshold
    if not mask.any():
        return None
    h, w = mask.shape
    hull = ndimage.binary_dilation(mask, iterations=pad)
    s = spacing_for(tag, int(mask.sum()))

    points = []
    contours, _ = cv2.findContours(hull.astype(np.uint8), cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    for contour in contours:
        c = contour[:, 0, :].astype(np.float32)
        if len(c) >= 3:
            points.append(c[:: max(1, int(s * 0.5))])

    ys, xs = np.nonzero(hull)
    gx = np.arange(xs.min(), xs.max() + s, s, dtype=np.float32)
    gy = np.arange(ys.min(), ys.max() + s, s, dtype=np.float32)
    grid = np.stack(np.meshgrid(gx, gy), -1).reshape(-1, 2)
    inner = ndimage.distance_transform_edt(hull)
    gxi = np.clip(grid[:, 0].astype(int), 0, w - 1)
    gyi = np.clip(grid[:, 1].astype(int), 0, h - 1)
    points.append(grid[inner[gyi, gxi] > s * 0.35])

    vertices = np.unique(np.round(np.concatenate(points), 1), axis=0)
    if len(vertices) < 3:
        return None
    triangles = Delaunay(vertices).simplices
    ids = np.full((h, w), -1, np.int32)
    for i, tri in enumerate(np.round(vertices[triangles]).astype(np.int32)):
        cv2.fillConvexPoly(ids, tri, int(i))
    touched = np.unique(ids[hull])
    triangles = triangles[touched[touched >= 0]]

    used = np.unique(triangles)
    remap = np.full(len(vertices), -1, np.int64)
    remap[used] = np.arange(len(used))
    return vertices[used].astype(np.float32), remap[triangles].astype(np.int32)


def uncovered_pixels(alpha, vertices, triangles, threshold=0.05):
    canvas = np.zeros(alpha.shape, np.uint8)
    pts = np.round(vertices[triangles]).astype(np.int32)
    cv2.fillPoly(canvas, list(pts), 1)
    return int(((alpha > threshold) & (canvas == 0)).sum())
