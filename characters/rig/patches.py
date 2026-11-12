"""Face patches: an edit of the pose image that changes only the face, grafted onto the rig as one part.

The edit is aligned with the base art (the whole figure, then the head) and cut by a fixed region made from the base
rig's own layers of that pose, so a faint smile comes through as fully as a laugh:

- `face`: the face layer shrunk by ERODE px from its outline (the base jaw line stays), covering eyes, brows, nose,
  mouth and cheeks; replaces all of them.
- `mouth`: a rounded box MOUTH_HALF_WIDTH face widths to each side of the mouth, from just below the nose to just
  above the chin, inside that face area; replaces the mouth.
- `eyes`: each eye grown by EYE_GROW of its size, inside the face area and clear of the brows; replaces the eyes.

A region also takes in everything the parts it replaces draw, so none of them leaves a hole. The cut is matched in
color to the base on a ring just outside it (without what the edit changed there) and feathered by FEATHER px. Under
the base layers that stay above it (the bangs; the brows of an eyes patch) it holds the base face as the rig draws it
rather than the edit's copy of those layers, so nothing doubles when they move.

Each cut reports its registration, how much of the region the edit changed (`changed`, against `noise`, the share it
changed around the head), how much of the cut's visible edge runs through that change (`edge`: a seam risk) and just
outside it (`outside`: what the cut leaves out, such as a redrawn jaw line), and the color gain and offset.
"""
import numpy as np
from scipy import ndimage

from . import layers as L
from . import register
from .skeleton import smoothstep

REPLACES = {
    "face": ("eyewhite", "irides", "eyelash", "eyebrow", "nose", "mouth"),
    "mouth": ("mouth",),
    "eyes": ("eyewhite", "irides", "eyelash"),
}
ERODE = 3.0
FEATHER = 1.5
MOUTH_HALF_WIDTH = 0.22
MOUTH_TOP_GAP, MOUTH_CHIN_GAP = 0.02, 0.02
EYE_GROW = 0.25
BROW_CLEARANCE = 2.0
# Registration ignores the region grown by this many face widths: the edit is free to change it.
KEEP_OUT = 0.05
COLOR_RING = (1.0, 8.0)
MIN_RING_PX = 200
# Spreads are matched only where the ring has some (ink, hair); a flat ring of skin matches the mean alone.
GAIN_RANGE, MIN_SPREAD = (0.8, 1.25), 0.02
# The edit gives way to the base face where the layers kept above the patch cover it by this much (from, to).
COVER_RAMP = (0.15, 0.6)
# A pixel changed when the edit and the art, both blurred by CHANGE_SIGMA px, differ by CHANGE_LEVEL in a channel.
# An edit must change MIN_CHANGED face widths squared of its region (a pucker of a small mouth is a few dozen px),
# and MIN_CHANGED_OVER_NOISE times the share it changed around the head (a redrawn but unchanged face).
CHANGE_SIGMA, CHANGE_LEVEL = 1.0, 0.12
MIN_CHANGED, MIN_CHANGED_OVER_NOISE = 0.001, 1.5
# A cut whose edge runs this much through changed content may show a seam.
EDGE_WARN = 0.25
MIN_INLIERS, MAX_RESIDUAL = 15, 1.5
# The patch follows the face bone at its edge and the features bone this many face widths inside it.
FEATURE_RAMP = 0.1


class Rejected(ValueError):
    """An edit the build leaves out: its registration failed, or it barely changed the region."""


def erode(mask, px):
    return ndimage.distance_transform_edt(mask) > px


def grow(mask, px):
    return ndimage.distance_transform_edt(~mask) <= px


def feather(mask, px=FEATHER):
    """Soft alpha crossing 0.5 on the mask's edge, ramping over `px` to either side of it."""
    inside = ndimage.distance_transform_edt(mask) - 0.5
    outside = ndimage.distance_transform_edt(~mask) - 0.5
    signed = np.where(mask, inside, -outside)
    return np.clip(0.5 + signed / (2 * px), 0, 1).astype(np.float32)


def _union(layers, threshold, shape):
    out = np.zeros(shape, bool)
    for layer in layers:
        out |= layer.alpha > threshold
    return out


def mouth_area(layers, face_box, shape):
    """A rounded box around the mouth: MOUTH_HALF_WIDTH face widths to each side of its center, from just below the
    nose (or a little above the mouth without one) to just above the chin."""
    fx0, _, fx1, chin = face_box
    fw = fx1 - fx0
    mouth = _union([layer for layer in layers if layer.tag == "mouth"], 0.2, shape)
    if not mouth.any():
        raise Rejected("the base has no mouth layer")
    ys, xs = np.nonzero(mouth)
    cx, top_of_mouth = (xs.min() + xs.max()) / 2, ys.min()
    nose = _union([layer for layer in layers if layer.tag == "nose"], 0.2, shape)
    top = np.nonzero(nose)[0].max() + MOUTH_TOP_GAP * fw if nose.any() else top_of_mouth - 0.1 * fw
    top = min(top, top_of_mouth - 0.03 * fw)
    bottom = chin - MOUTH_CHIN_GAP * fw
    cy, half_h, half_w = (top + bottom) / 2, max(1.0, (bottom - top) / 2), MOUTH_HALF_WIDTH * fw
    yy, xx = np.mgrid[:shape[0], :shape[1]]
    return np.abs((xx - cx) / half_w) ** 4 + np.abs((yy - cy) / half_h) ** 4 <= 1


def eye_area(layers, shape):
    """Each eye (white, iris and lashes) grown by EYE_GROW of its larger side, clear of the brows."""
    eyes = [layer for layer in layers if layer.tag in REPLACES["eyes"]]
    if not eyes:
        raise Rejected("the base has no eye layers")
    out = np.zeros(shape, bool)
    for side in {layer.side for layer in eyes}:
        eye = _union([layer for layer in eyes if layer.side == side], 0.2, shape)
        if not eye.any():
            continue
        ys, xs = np.nonzero(eye)
        out |= grow(eye, EYE_GROW * max(np.ptp(xs), np.ptp(ys)) + 1)
    brows = _union([layer for layer in layers if layer.tag == "eyebrow"], 0.2, shape)
    return out & ~grow(brows, BROW_CLEARANCE) if brows.any() else out


class Base:
    """The base rig's face and art for one pose; every patch of the pose is cut from it."""

    def __init__(self, layers, reference, face_box):
        self.layers, self.reference = layers, reference
        self.shape = reference.shape[:2]
        self.box = face_box
        fx0, fy0, fx1, fy1 = face_box
        self.fw, fh = fx1 - fx0, fy1 - fy0
        h, w = self.shape
        self.head = (max(0, fx0 - 0.6 * self.fw), max(0, fy0 - 0.5 * fh), min(w, fx1 + 0.6 * self.fw),
                     min(h, fy1 + 0.4 * fh))
        self.face_index = next(i for i, layer in enumerate(layers) if layer.tag == "face")
        self.face = layers[self.face_index].alpha > 0.5
        self.skin = erode(self.face, ERODE)
        self.drawn = L.coverage(layers) > 0.5
        self._regions = {}

    def head_mask(self):
        x0, y0, x1, y1 = (int(round(v)) for v in self.head)
        out = np.zeros(self.shape, bool)
        out[y0:y1, x0:x1] = True
        return out

    def region(self, name):
        """The region's mask, its feathered alpha, the base parts it replaces, the base face under the layers kept
        above it, and how much those layers cover."""
        if name not in REPLACES:
            raise ValueError(f"region must be one of {sorted(REPLACES)}, not {name!r}")
        if name in self._regions:
            return self._regions[name]
        tags = REPLACES[name]
        replaced = [layer for layer in self.layers if layer.tag in tags]
        if name == "face":
            mask = self.skin.copy()
        elif name == "mouth":
            mask = mouth_area(self.layers, self.box, self.shape) & self.skin
        else:
            mask = eye_area(self.layers, self.shape) & self.skin
        if replaced:
            mask |= grow(_union(replaced, 0.05, self.shape) & self.head_mask(), 1)
        kept = [layer for layer in self.layers[self.face_index + 1:] if layer.tag not in tags]
        out = {
            "mask": mask,
            "alpha": feather(mask),
            "replaces": [layer.name for layer in replaced],
            "under": L.composite([self.layers[self.face_index]] + replaced),
            "cover": L.coverage(kept) if kept else np.zeros(self.shape, np.float32),
        }
        self._regions[name] = out
        return out


def align(path, reference, size, head_box, keep_out):
    """Aligns an edit with the base art: a similarity transform from the whole figure with the head left out, refined
    on the head with `keep_out` (what the edit may change) left out. The edit is first resampled once to the art's
    scale, so the final warp barely resamples it again. Returns the edit in canvas pixels and the report."""
    x0, y0, x1, y1 = (int(round(v)) for v in head_box)
    head = np.zeros(reference.shape[:2], bool)
    head[y0:y1, x0:x1] = True
    try:
        coarse = L.load_reference(path, size)
        matrix, whole = register.similarity(coarse, reference, ignore=head)
        k = float(np.hypot(matrix[0, 0], matrix[1, 0]))
        scaled = (max(1, round(size[0] * k)), max(1, round(size[1] * k)))
        fine = L.load_reference(path, scaled)
        kx, ky = scaled[0] / size[0], scaled[1] / size[1]
        to_coarse = np.array([[1 / kx, 0, 0.5 / kx - 0.5], [0, 1 / ky, 0.5 / ky - 0.5]])
        matrix = register.compose(matrix, to_coarse)
        rough = register.warp_rgb(fine, matrix, size)
        local, local_report = register.similarity(rough[y0:y1, x0:x1], reference[y0:y1, x0:x1],
                                                  ignore=keep_out[y0:y1, x0:x1])
    except ValueError as e:
        raise Rejected(f"registration failed: {e}") from None
    shift = np.array([[1, 0, x0], [0, 1, y0]], np.float64)
    unshift = np.array([[1, 0, -x0], [0, 1, -y0]], np.float64)
    matrix = register.compose(shift, local, unshift, matrix)
    report = {"whole": _brief(whole), "head": _brief(local_report)}
    if local_report["inliers"] < MIN_INLIERS or local_report["residual_px"] > MAX_RESIDUAL:
        raise Rejected(f"registration failed on the head: {local_report['inliers']} inliers, "
                       f"{local_report['residual_px']} px residual")
    return register.warp_rgb(fine, matrix, size), report


def _brief(r):
    return {k: r[k] for k in ("inliers", "residual_px", "scale", "angle_deg", "shift_px")}


def match_colors(image, reference, ring):
    """Matches each channel's mean and spread on `ring` to the reference. Returns the image and (gain, offset)."""
    out = image.copy()
    gains, offsets = [], []
    for c in range(3):
        e, b = image[..., c][ring], reference[..., c][ring]
        flat = min(e.std(), b.std()) < MIN_SPREAD
        gain = 1.0 if flat else float(np.clip(b.std() / e.std(), *GAIN_RANGE))
        offset = float(b.mean() - gain * e.mean())
        out[..., c] = image[..., c] * gain + offset
        gains.append(round(gain, 3))
        offsets.append(round(offset * 255, 1))
    return np.clip(out, 0, 1), (gains, offsets)


def _share(flags, where):
    return round(float(flags[where].mean()), 3) if where.any() else 0.0


def changed(image, reference):
    blur = [ndimage.gaussian_filter(x, (CHANGE_SIGMA, CHANGE_SIGMA, 0)) for x in (image, reference)]
    return np.abs(blur[0] - blur[1]).max(axis=2) > CHANGE_LEVEL


def cut(base, path, region):
    """Cuts one patch from the edit at `path`. Returns (straight RGBA on the canvas, replaced part names, report);
    raises Rejected when the registration fails or the region barely changed."""
    r = base.region(region)
    mask, size = r["mask"], base.shape[::-1]
    edit, registration = align(path, base.reference, size, base.head, grow(mask, KEEP_OUT * base.fw))
    ring = grow(mask, COLOR_RING[1]) & ~grow(mask, COLOR_RING[0])
    # What the edit changed around the region (a squeezed eye, a tear) would skew the spread: match on the rest.
    clean = ring & ~grow(changed(edit, base.reference), 2)
    edit, (gain, offset) = match_colors(edit, base.reference, clean if clean.sum() >= MIN_RING_PX else ring)
    visible = mask & (r["cover"] < COVER_RAMP[0])
    diff = changed(edit, base.reference)
    control = base.head_mask() & base.drawn & ~grow(mask, 0.15 * base.fw)
    noise = float(diff[control].mean()) if control.any() else 0.0
    moved = int(diff[visible].sum())
    share = moved / max(1, int(visible.sum()))
    edge = _share(diff, visible & ~erode(mask, 2))
    report = {"region": region, "registration": registration, "area_px": int(mask.sum()),
              "changed_px": moved, "changed": round(share, 4), "noise": round(noise, 4), "edge": edge,
              "outside": _share(diff, grow(mask, 3) & ~mask & (r["cover"] < COVER_RAMP[0]) & base.drawn),
              "gain": gain, "offset": offset}
    if edge > EDGE_WARN:
        report["warning"] = "the cut's edge runs through what the edit changed: check for a seam"
    if moved < MIN_CHANGED * base.fw ** 2 or share < MIN_CHANGED_OVER_NOISE * noise:
        raise Rejected(f"the {region} region barely changed: {moved} px ({share:.2%} of it, "
                       f"{noise:.2%} around the head)")
    w = smoothstep(COVER_RAMP[0], COVER_RAMP[1], r["cover"])[..., None]
    rgba = np.zeros(base.shape + (4,), np.float32)
    rgba[..., :3] = edit * (1 - w) + r["under"] * w
    rgba[..., 3] = r["alpha"]
    return rgba, r["replaces"], report


def weights(vertices, alpha, face_width):
    """Follows the face bone at the patch's edge and the features bone inside it, as the base face and its features
    do when the head turns."""
    depth = ndimage.distance_transform_edt(alpha > 0.5)
    h, w = depth.shape
    x = np.clip(np.round(vertices[:, 0]).astype(int), 0, w - 1)
    y = np.clip(np.round(vertices[:, 1]).astype(int), 0, h - 1)
    t = smoothstep(0.0, FEATURE_RAMP * face_width, depth[y, x])
    return [("features", t), ("face", 1 - t)]
