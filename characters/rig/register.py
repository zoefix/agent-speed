"""Aligns a generated pose or expression image with the base illustration (similarity transform from SIFT matches)."""
import cv2
import numpy as np


def _gray(rgb):
    return cv2.cvtColor((np.clip(rgb, 0, 1) * 255).astype(np.uint8), cv2.COLOR_RGB2GRAY)


def similarity(moving_rgb, fixed_rgb, ignore=None):
    """Returns (2x3 matrix mapping moving -> fixed, report). `ignore` masks fixed-image areas expected to change."""
    sift = cv2.SIFT_create(nfeatures=6000)
    mask_fixed = None if ignore is None else (~ignore).astype(np.uint8) * 255
    kp_m, des_m = sift.detectAndCompute(_gray(moving_rgb), None)
    kp_f, des_f = sift.detectAndCompute(_gray(fixed_rgb), mask_fixed)
    if des_m is None or des_f is None:
        raise ValueError("no features to register")
    pairs = cv2.BFMatcher(cv2.NORM_L2).knnMatch(des_m, des_f, k=2)
    good = [m for m, n in (p for p in pairs if len(p) == 2) if m.distance < 0.72 * n.distance]
    if len(good) < 12:
        raise ValueError(f"only {len(good)} feature matches")
    src = np.float32([kp_m[m.queryIdx].pt for m in good])
    dst = np.float32([kp_f[m.trainIdx].pt for m in good])
    matrix, inliers = cv2.estimateAffinePartial2D(src, dst, method=cv2.RANSAC, ransacReprojThreshold=2.0,
                                                  maxIters=20000, confidence=0.999)
    if matrix is None:
        raise ValueError("registration failed")
    keep = inliers.ravel().astype(bool)
    moved = cv2.transform(src[keep][:, None, :], matrix)[:, 0, :]
    residual = float(np.sqrt(((moved - dst[keep]) ** 2).sum(axis=1)).mean())
    scale = float(np.hypot(matrix[0, 0], matrix[1, 0]))
    angle = float(np.degrees(np.arctan2(matrix[1, 0], matrix[0, 0])))
    report = {"matches": len(good), "inliers": int(keep.sum()), "residual_px": round(residual, 3),
              "scale": round(scale, 4), "angle_deg": round(angle, 3),
              "shift_px": [round(float(matrix[0, 2]), 2), round(float(matrix[1, 2]), 2)]}
    return matrix, report


def warp_rgba(rgba, matrix, size):
    """Warps straight-alpha RGBA through premultiplied space so edges keep their color."""
    pre = rgba.copy()
    pre[..., :3] *= pre[..., 3:4]
    out = cv2.warpAffine(pre, matrix, size, flags=cv2.INTER_CUBIC, borderMode=cv2.BORDER_CONSTANT, borderValue=0)
    out = np.clip(out, 0, 1)
    a = out[..., 3:4]
    out[..., :3] = np.where(a > 1e-4, out[..., :3] / np.maximum(a, 1e-4), 0)
    return np.clip(out, 0, 1)
