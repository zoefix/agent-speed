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


def take_prop(layers, mask, reference, name="prop", grow=2, reach=15, share=0.6):
    """Moves a held object (segment.prop_mask, grown by `grow` px for its outline) out of the layers that draw it into
    one `objects` layer, so it moves as one piece: opaque inside the mask (also where the split left the art out of
    every layer), as opaque as the layers were on the grown rim. A layer gives up its pixels there when it draws above
    the object's slot in the draw order, or is the visible owner of the pixel or above it; what lies hidden below the
    owner stays (the body behind a hugged doll), unless it is a piece of the layer lying at least `share` within
    `reach` px of the object: the split paints hair behind a doll's face as if it were a second head, which would
    show once the doll moves. Such a piece joins the object. Returns the layers with the object in its slot."""
    core = mask
    if grow:
        mask = ndimage.binary_dilation(mask, iterations=grow)
    near = ndimage.binary_dilation(mask, iterations=reach)
    total = np.where(core, 1.0, coverage(layers))
    owner = owner_map(layers)
    slot = next((k for k, layer in enumerate(layers)
                 if DRAW_ORDER.index(layer.tag) > DRAW_ORDER.index("objects")), len(layers))
    rgba = np.zeros_like(layers[0].rgba)
    rgba[..., :3] = reference
    rgba[..., 3] = np.where(mask, total, 0.0)
    for k, layer in enumerate(layers):
        give = mask & ((k >= slot) | (owner <= k))
        labels, count = ndimage.label(layer.alpha > 0.12)
        if count:
            ids = np.arange(1, count + 1)
            touching = np.asarray(ndimage.maximum(mask, labels, ids)) > 0
            within = np.asarray(ndimage.mean(near, labels, ids)) >= share
            piece = np.isin(labels, ids[touching & within])
            piece = ndimage.binary_dilation(piece, iterations=1) & (layer.alpha > 0)
            joins = piece & ~mask
            rgba[..., :3] = np.where(joins[..., None], layer.rgba[..., :3], rgba[..., :3])
            rgba[..., 3] = np.where(joins, np.maximum(rgba[..., 3], layer.alpha), rgba[..., 3])
            give |= piece
        layer.rgba[..., 3] = np.where(give, 0.0, layer.alpha)
    return layers[:slot] + [Layer(name, "objects", rgba)] + layers[slot:]


def split_objects(layers, min_share=0.05, bridge=4):
    """Splits each objects layer the split made (not a prop) into its separate pieces (gaps up to 2 * `bridge` px
    closed, pieces of at least `min_share` of the layer; smaller bits join the nearest piece), so a fan in one hand
    and a purse in the other, or hanging from the waist, can each follow their own holder."""
    out = []
    for layer in layers:
        if layer.tag != "objects" or layer.name.startswith("prop"):
            out.append(layer)
            continue
        solid = layer.alpha > 0.3
        labels, count = ndimage.label(ndimage.binary_closing(solid, iterations=bridge) | solid)
        sizes = np.asarray(ndimage.sum(solid, labels, range(1, count + 1))) if count else np.zeros(0)
        big = [i + 1 for i, size in enumerate(sizes) if size >= min_share * sizes.sum()]
        if len(big) < 2:
            out.append(layer)
            continue
        _, (iy, ix) = ndimage.distance_transform_edt(~np.isin(labels, big), return_indices=True)
        piece = labels[iy, ix]
        for k, i in enumerate(big):
            rgba = layer.rgba.copy()
            rgba[..., 3] = np.where(piece == i, rgba[..., 3], 0.0)
            out.append(Layer(layer.name if k == 0 else f"{layer.name}{k + 1}", layer.tag, rgba))
    return out


HEAD_TAGS = ("face", "front hair", "back hair", "headwear")


def lift_hats(layers, face_box, above=0.3, min_px=1000, overlap=0.35):
    """Gives the head what the split drew of it into the top: topwear above the upper `above` of the face (a white
    beret the split takes for the white blouse) becomes a `headwear` layer named `hat`, so it turns with the head.
    It keeps the top's place in the draw order while it sits on the head; a hat the art shows tossed above the head
    (the median gap between its lower edge and the head's top, column by column, over a tenth of its height; a stray
    strand of hair may still touch it) is set down on it, its lower `overlap` over the head's top, and drawn over the
    hair.
    Returns the layers and how many hats it lifted."""
    fx0, fy0, fx1, fy1 = face_box
    cut = int(fy0 + above * (fy1 - fy0))
    head = np.zeros(layers[0].alpha.shape, bool)
    for layer in layers:
        if layer.tag in HEAD_TAGS:
            head |= layer.alpha > 0.5
    out, seated, lifted = [], [], 0
    for layer in layers:
        out.append(layer)
        if layer.tag != "topwear":
            continue
        hat = np.zeros(layer.alpha.shape, bool)
        hat[:cut] = layer.alpha[:cut] > 0.02
        if (layer.alpha[:cut] > 0.5).sum() < min_px:
            continue
        rgba = layer.rgba.copy()
        rgba[..., 3] = np.where(hat, rgba[..., 3], 0.0)
        layer.rgba[..., 3] = np.where(hat, 0.0, layer.alpha)
        lifted += 1
        ys, xs = np.nonzero(hat)
        gaps = []
        for x in range(xs.min(), xs.max() + 1):
            hat_rows, head_rows = np.nonzero(hat[:, x])[0], np.nonzero(head[:, x])[0]
            if len(hat_rows) and len(head_rows):
                gaps.append(head_rows.min() - hat_rows.max())
        height = ys.max() - ys.min()
        if not gaps or np.median(gaps) <= 0.1 * height:
            out.append(Layer("hat", "headwear", rgba))
            continue
        dy = int(np.median(gaps) + overlap * height)
        moved = np.zeros_like(rgba)
        moved[dy:] = rgba[:rgba.shape[0] - dy]
        seated.append(Layer("hat", "headwear", moved))
    return out + seated, lifted


def drop_empty(layers, threshold=0.12):
    """Layers left with nothing solid once the art is painted onto its owners (a faint nose the face covers) or split
    by the arms (a sleeve whose other half went with a held doll)."""
    return [layer for layer in layers if (layer.alpha > threshold).any()]


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


def _segment_distance(xx, yy, a, b):
    ab = b - a
    t = np.clip(((xx - a[0]) * ab[0] + (yy - a[1]) * ab[1]) / max(float(ab @ ab), 1e-9), 0, 1)
    return np.hypot(xx - (a[0] + t * ab[0]), yy - (a[1] + t * ab[1]))


def split_by_arms(layers, arms, reference=None, core=4.0):
    """Splits the arm layer that split_pairs left whole (both sleeves drawn as one blob, as when both hands hold a
    flute on one side) between the two arms the pose model found. With the `reference` art, each arm grows from its
    bones (pixels within `core` px of shoulder-elbow-wrist) over the art's gradient (a watershed), so cloth stays with
    the arm it is drawn continuous with and the split follows the ink lines: a wide sleeve hanging far below its own
    forearm is not handed to the other hand in front of it. Pixels neither arm reaches, or every pixel without the
    art, go to the nearer arm (shoulder, elbow, wrist and half a forearm past the wrist for the hand)."""
    if not {"left", "right"} <= set(arms):
        return layers
    out = []
    for layer in layers:
        if layer.tag != "handwear" or layer.side is not None:
            out.append(layer)
            continue
        yy, xx = np.mgrid[0:layer.alpha.shape[0], 0:layer.alpha.shape[1]].astype(np.float32)
        dist, bone = {}, {}
        for side in ("left", "right"):
            shoulder, elbow, wrist = (np.asarray(arms[side][k], np.float64) for k in ("shoulder", "elbow", "wrist"))
            reach = wrist + 0.5 * (wrist - elbow)
            segments = [_segment_distance(xx, yy, a, b) for a, b in ((shoulder, elbow), (elbow, wrist), (wrist, reach))]
            dist[side] = np.minimum.reduce(segments)
            bone[side] = np.minimum(segments[0], segments[1])
        left = dist["left"] <= dist["right"]
        if reference is not None:
            from skimage.segmentation import watershed
            mask = layer.alpha > 0.02
            markers = np.zeros(mask.shape, np.int32)
            markers[mask & (bone["left"] < core) & (bone["right"] >= core)] = 1
            markers[mask & (bone["right"] < core) & (bone["left"] >= core)] = 2
            grown = watershed(ndimage.gaussian_gradient_magnitude(reference.mean(axis=2), 1.2), markers, mask=mask)
            left = np.where(grown == 1, True, np.where(grown == 2, False, left))
        for side, keep in (("left", left), ("right", ~left)):
            rgba = layer.rgba.copy()
            rgba[..., 3] = np.where(keep, rgba[..., 3], 0)
            out.append(Layer(f"{layer.name}.{side}", layer.tag, rgba, side))
    return out


def split_body_from_arms(layers, arms, reference, face_box, core=4.0, inset=0.15, clear=0.35):
    """Cuts out of each arm layer the body the split drew into it (the front of a robe beside a wide sleeve), along
    the art's ink lines: the arm grows from its bones (pixels within `core` px of shoulder-elbow-wrist), the body from
    the torso (between the shoulders, `inset` face widths in from each, below the upper face, over `clear` face widths
    from the arm's bones so a forearm across the chest seeds no body), over the art's gradient (a watershed), so a
    sleeve stays with its arm up to the line that parts it from the robe. What the
    body reaches becomes a `topwear` layer right after the arm, so it keeps its place in the draw order and moves with
    the chest and hips. Returns the layers and the pixels moved."""
    if not {"left", "right"} <= set(arms):
        return layers, 0
    from skimage.segmentation import watershed
    fx0, fy0, fx1, fy1 = face_box
    face_w, face_h = fx1 - fx0, fy1 - fy0
    xs = sorted(arms[side]["shoulder"][0] for side in ("left", "right"))
    gradient = ndimage.gaussian_gradient_magnitude(reference.mean(axis=2), 1.2)
    yy, xx = np.mgrid[0:reference.shape[0], 0:reference.shape[1]].astype(np.float32)
    torso = (xx > xs[0] + inset * face_w) & (xx < xs[1] - inset * face_w) & (yy > fy0 + 0.5 * face_h)
    out, moved = [], 0
    for layer in layers:
        out.append(layer)
        if layer.tag != "handwear" or layer.side not in arms:
            continue
        mask = layer.alpha > 0.02
        shoulder, elbow, wrist = (np.asarray(arms[layer.side][k], np.float64) for k in ("shoulder", "elbow", "wrist"))
        bone = np.minimum(_segment_distance(xx, yy, shoulder, elbow), _segment_distance(xx, yy, elbow, wrist))
        markers = np.zeros(mask.shape, np.int32)
        markers[mask & (bone < core)] = 1
        markers[mask & torso & (bone >= max(4 * core, clear * face_w))] = 2
        if not (markers == 2).any():
            continue
        body = watershed(gradient, markers, mask=mask) == 2
        if body.sum() < 200:
            continue
        rgba = layer.rgba.copy()
        rgba[..., 3] = np.where(body, rgba[..., 3], 0.0)
        layer.rgba[..., 3] = np.where(body, 0.0, layer.alpha)
        out.append(Layer(f"{layer.name} body", "topwear", rgba))
        moved += int((body & (rgba[..., 3] > 0.5)).sum())
    return out, moved


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
