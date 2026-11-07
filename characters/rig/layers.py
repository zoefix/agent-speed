"""See-Through PSD layers: semantic draw order, reprojection of the original art, left/right split, underlay growth."""
from dataclasses import dataclass

import numpy as np
from PIL import Image
from psd_tools import PSDImage
from scipy import ndimage

DRAW_ORDER = [
    "back hair", "wings", "tail", "footwear", "legwear", "bottomwear", "neck", "topwear", "neckwear",
    "objects", "handwear", "hand", "ears", "earwear", "face", "eyewhite", "irides", "eyelash", "eyebrow",
    "nose", "mouth", "eyewear", "front hair", "headwear",
]
PAIRED = {"handwear", "ears", "eyewhite", "irides", "eyelash", "eyebrow"}
NO_GROWTH = {"eyewhite", "irides", "eyelash", "eyebrow", "nose", "mouth", "eyewear", "earwear"}
HIDDEN_TAGS = ("handwear", "hand", "objects")


@dataclass
class Layer:
    name: str
    tag: str
    rgba: np.ndarray
    side: str | None = None

    @property
    def alpha(self):
        return self.rgba[..., 3]


def load_psd(path):
    psd = PSDImage.open(path)
    size = (psd.width, psd.height)
    layers = {}
    for item in psd.descendants():
        if item.is_group():
            continue
        img = item.topil()
        if img is None:
            continue
        full = Image.new("RGBA", size, (0, 0, 0, 0))
        full.paste(img.convert("RGBA"), (item.left, item.top))
        tag = item.name.strip()
        rgba = np.asarray(full, np.float32) / 255
        if rgba[..., 3].max() > 0.02:
            layers[tag] = rgba
    ordered = [Layer(tag, tag, layers[tag]) for tag in DRAW_ORDER if tag in layers]
    unknown = sorted(set(layers) - set(DRAW_ORDER))
    if unknown:
        raise ValueError(f"unknown See-Through tags: {unknown}")
    return size, ordered


def load_reference(path, size):
    src = Image.open(path).convert("RGBA")
    side = max(src.size)
    square = Image.new("RGBA", (side, side), (255, 255, 255, 255))
    square.alpha_composite(src, ((side - src.width) // 2, (side - src.height) // 2))
    return np.asarray(square.convert("RGB").resize(size, Image.LANCZOS), np.float32) / 255


def coverage(layers):
    return 1 - np.prod([1 - layer.alpha for layer in layers], axis=0)


def owner_map(layers):
    """Index of the topmost mostly opaque layer per pixel, -1 where none is."""
    owner = np.full(layers[0].alpha.shape, -1, np.int32)
    for i, layer in enumerate(layers):
        owner[layer.alpha > 0.5] = i
    return owner


def reproject(layers, reference, background=1.0):
    """Paints the original art onto the layer that owns each visible pixel, so the rest pose equals the original."""
    total = coverage(layers)
    owner = owner_map(layers)
    interior = total > 0.95
    m = np.clip(total, 1e-3, 1)[..., None]
    unblended = np.clip((reference - background * (1 - m)) / m, 0, 1)
    for i, layer in enumerate(layers):
        mine = owner == i
        layer.rgba[..., :3] = np.where(mine[..., None], unblended, layer.rgba[..., :3])
        layer.rgba[..., 3] = np.where(mine & interior, 1.0, layer.alpha)
        layer.rgba[..., 3] = np.where((owner >= 0) & (owner < i) & interior, 0.0, layer.alpha)
    return owner


def split_pairs(layers, center_x, min_share=0.03):
    """Splits paired parts (arms, eyes, ears) into image-left and image-right layers."""
    out = []
    for layer in layers:
        if layer.tag not in PAIRED:
            out.append(layer)
            continue
        solid = layer.alpha > 0.12
        labels, count = ndimage.label(solid)
        if count == 0:
            continue
        sizes = ndimage.sum(solid, labels, range(1, count + 1))
        keep = [i + 1 for i, s in enumerate(sizes) if s >= min_share * sizes.max()]
        centers = ndimage.center_of_mass(solid, labels, keep)
        left_ids = [k for k, (_, cx) in zip(keep, centers) if cx < center_x]
        right_ids = [k for k, (_, cx) in zip(keep, centers) if cx >= center_x]
        if not left_ids or not right_ids:
            out.append(layer)
            continue
        left_seed = np.isin(labels, left_ids)
        right_seed = np.isin(labels, right_ids)
        dist_l = ndimage.distance_transform_edt(~left_seed)
        dist_r = ndimage.distance_transform_edt(~right_seed)
        for side, mask in (("left", dist_l <= dist_r), ("right", dist_l > dist_r)):
            rgba = layer.rgba.copy()
            rgba[..., 3] = np.where(mask, rgba[..., 3], 0)
            out.append(Layer(f"{layer.name}.{side}", layer.tag, rgba, side))
    return out


def grow_underlays(layers, radius=10):
    """Extends each layer under the opaque layers above it, so moving parts never open gaps."""
    for i, layer in enumerate(layers):
        if layer.tag in NO_GROWTH or i == len(layers) - 1:
            continue
        above = coverage(layers[i + 1:]) > 0.98
        solid = layer.alpha > 0.5
        if not solid.any():
            continue
        grow = ndimage.binary_dilation(solid, iterations=radius) & above & ~solid
        if not grow.any():
            continue
        _, (iy, ix) = ndimage.distance_transform_edt(~solid, return_indices=True)
        layer.rgba[..., :3] = np.where(grow[..., None], layer.rgba[iy, ix, :3], layer.rgba[..., :3])
        layer.rgba[..., 3] = np.where(grow, 1.0, layer.alpha)


def _behind_arms(layers, margin):
    hidden_ids = [i for i, layer in enumerate(layers) if layer.tag in HIDDEN_TAGS]
    return hidden_ids, ndimage.binary_dilation(np.isin(owner_map(layers), hidden_ids), iterations=margin)


def _revealed(backdrop, guard):
    return ~ndimage.binary_dilation(backdrop["hidden"] > 0.02, iterations=guard)


def fill_hidden(layers, backdrops, guard=4):
    """Repaints what the arms and the held object cover with what the pose variants show there.

    See-Through guesses the art behind an arm; once the arm moves that guess shows. Each backdrop is a variant image
    split into layers and aligned to the base canvas. Every area behind an arm takes as much as it can from the one
    variant that reveals most of it. Only pixels the arms cover fully, on layers drawn below them, change, so the
    rest pose stays the same.
    """
    hidden_ids = [i for i, layer in enumerate(layers) if layer.tag in HIDDEN_TAGS]
    behind = coverage([layers[i] for i in hidden_ids]) > 0.98
    floor = min(hidden_ids)
    index = {layer.name: i for i, layer in enumerate(layers)}
    by_tag = {}
    for i, layer in enumerate(layers):
        by_tag.setdefault(layer.tag, i)
    fallback = by_tag.get("topwear", floor - 1)
    revealed = [_revealed(b, guard) for b in backdrops]
    targets = []
    for b in backdrops:
        lookup = np.array([index.get(n, by_tag.get(t, -2)) for n, t in zip(b["names"], b["tags"])] + [-1], np.int32)
        lookup = np.where(lookup >= floor, fallback, lookup)
        targets.append(np.where(b["coverage"] > 0.02, lookup[b["owner"]], -1))
    labels, count = ndimage.label(behind)
    todo = behind.copy()
    filled = 0
    for k in range(1, count + 1):
        area = labels == k
        for i in sorted(range(len(backdrops)), key=lambda i: -int((area & revealed[i]).sum())):
            free = area & todo & revealed[i] & (targets[i] != -2)
            if not free.any():
                continue
            b, target = backdrops[i], np.where(free, targets[i], -3)
            for j in range(floor):
                layer = layers[j]
                mine = target == j
                layer.rgba[..., :3] = np.where(mine[..., None], b["rgb"], layer.rgba[..., :3])
                layer.rgba[..., 3] = np.where(mine, b["coverage"], layer.alpha)
                cleared = (target == -1) | ((target >= 0) & (target < j))
                layer.rgba[..., 3] = np.where(cleared, 0.0, layer.alpha)
            filled += int(free.sum())
            todo &= ~free
    return filled, int(todo.sum())


def backdrop_patch(layers, backdrop, name, side, margin=3, guard=4):
    """One variant's own art for the area its replaced arm used to cover, plus the tag that owns each pixel."""
    _, behind = _behind_arms(layers, margin)
    region = behind & _revealed(backdrop, guard)
    alpha = np.where(region, backdrop["coverage"], 0.0).astype(np.float32)
    tags = np.array(backdrop["tags"] + [""])[backdrop["owner"]]
    return Layer(name, "backdrop", np.concatenate([backdrop["rgb"], alpha[..., None]], axis=2), side), tags


def composite(layers, background=1.0):
    out = np.full(layers[0].rgba.shape[:2] + (3,), background, np.float32)
    for layer in layers:
        a = layer.alpha[..., None]
        out = layer.rgba[..., :3] * a + out * (1 - a)
    return out
