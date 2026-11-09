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


FRONT_OF_TAIL = ("legwear", "bottomwear", "topwear")


def _smooth_fill(rgb, known, sigmas=(2, 4, 8, 16, 32, 64)):
    """Fills the pixels not `known` with a blur of the known ones, from fine to coarse: smooth colors from the
    surroundings, without the streaks a diffusion inpaint draws across a large area."""
    out = rgb.copy()
    todo = ~known
    weight = known.astype(np.float32)
    for sigma in sigmas:
        if not todo.any():
            break
        w = ndimage.gaussian_filter(weight, sigma)
        filled = np.stack([ndimage.gaussian_filter(rgb[..., c] * weight, sigma) for c in range(3)], axis=-1)
        ok = todo & (w > 1e-3)
        out[ok] = filled[ok] / w[ok][:, None]
        todo &= ~ok
    return out


def _repaint_copy(layer, copy, part, close=15, pad=40):
    """Repaints what `layer` drew of another part at `copy`: filled smoothly from its surroundings inside the layer's
    own outline (its pixels without the copy, closed by `close` px, holes filled), transparent outside it where the
    `part` itself is opaque (so its soft rim never shows the backdrop at rest). Returns the pixels made
    transparent."""
    h, w = copy.shape
    own = layer.alpha > 0.5
    outline = ndimage.binary_fill_holes(ndimage.binary_closing(own & ~copy, iterations=close))
    ys, xs = np.nonzero(copy)
    box = (slice(max(ys.min() - pad, 0), min(ys.max() + pad, h)), slice(max(xs.min() - pad, 0), min(xs.max() + pad, w)))
    filled = _smooth_fill(layer.rgba[box][..., :3], (own & ~copy)[box])
    inside = (copy & outline)[box]
    view = layer.rgba[box]
    view[..., :3] = np.where(inside[..., None], filled, view[..., :3])
    view[..., 3] = np.where(inside, 1.0, view[..., 3])
    cleared = copy & ~outline & part
    layer.rgba[..., 3] = np.where(cleared, 0.0, layer.alpha)
    return cleared


def tail_from_hair(layers, reference, face_box, core=3, min_px=2000, below=0.8, taper=0.5):
    """Gives the tail what the split drew of it into the front hair (a strand hanging from the head over the tail
    takes the tail with it), along the art's ink lines: a watershed over the art's gradient grows the hair from its
    pixels above the chin and the tail from the hair pixels within `core` px of the tail layer. What the tail reaches,
    from where it narrows below `taper` of its widest row down, joins the tail layer when it is at least `min_px` and
    `below` of it lies below the chin. Returns the pixels moved."""
    tail = next((layer for layer in layers if layer.tag == "tail"), None)
    hair = next((layer for layer in layers if layer.tag == "front hair"), None)
    if tail is None or hair is None:
        return 0
    from skimage.segmentation import watershed
    chin = face_box[3]
    mask = hair.alpha > 0.02
    near = ndimage.binary_dilation(tail.alpha > 0.5, iterations=core)
    markers = np.zeros(mask.shape, np.int32)
    markers[:int(chin)][mask[:int(chin)]] = 1
    markers[mask & near & (markers == 0)] = 2
    if not (markers == 2).any():
        return 0
    gradient = ndimage.gaussian_gradient_magnitude(reference.mean(axis=2), 1.2)
    piece = (watershed(gradient, markers, mask=mask) == 2) & (hair.alpha > 0.02)
    if not piece.any():
        return 0
    # A tail is wide where a strand of hair is narrow: going up from its widest row, the tail ends where the piece
    # narrows below `taper` of that width; what is above stays hair.
    widths = piece.sum(axis=1)
    peak = int(np.argmax(widths))
    top = peak
    while top > 0 and widths[top - 1] >= taper * widths[peak]:
        top -= 1
    piece[:top] = False
    ys = np.nonzero(piece)[0]
    if piece.sum() < min_px or (ys > chin).mean() < below:
        return 0
    tail.rgba[..., :3] = np.where(piece[..., None], hair.rgba[..., :3], tail.rgba[..., :3])
    tail.rgba[..., 3] = np.where(piece, np.maximum(tail.alpha, hair.alpha), tail.alpha)
    hair.rgba[..., 3] = np.where(piece, 0.0, hair.alpha)
    return int(piece.sum())


def tail_from_back_hair(layers, mask, reach=3, min_px=500):
    """Gives the tail what the split drew of it into the back hair (the top of a tail behind long hair), so it swings
    with the rest: the back hair's pixels inside `mask` (segment.tail_mask) in pieces that touch the tail layer
    (within `reach` px) and hold at least `min_px` join the tail, and the back hair is repainted there
    (_repaint_copy). Returns the pixels moved."""
    tail = next((layer for layer in layers if layer.tag == "tail"), None)
    hair = next((layer for layer in layers if layer.tag == "back hair"), None)
    if tail is None or hair is None or mask is None:
        return 0
    candidate = mask & (hair.alpha > 0.02) & ~(tail.alpha > 0.5)
    labels, count = ndimage.label(candidate)
    if not count:
        return 0
    near = ndimage.binary_dilation(tail.alpha > 0.5, iterations=reach)
    sizes = np.asarray(ndimage.sum(candidate, labels, range(1, count + 1)))
    touching = np.asarray(ndimage.maximum(near, labels, range(1, count + 1))) > 0
    piece = np.isin(labels, 1 + np.flatnonzero(touching & (sizes >= min_px)))
    if not piece.any():
        return 0
    tail.rgba[..., :3] = np.where(piece[..., None], hair.rgba[..., :3], tail.rgba[..., :3])
    tail.rgba[..., 3] = np.where(piece, np.maximum(tail.alpha, hair.alpha), tail.alpha)
    cleared = _repaint_copy(hair, piece, piece)
    inside = ndimage.binary_erosion(ndimage.binary_fill_holes(tail.alpha > 0.5), iterations=1)
    tail.rgba[..., 3] = np.where(cleared & inside, 1.0, tail.alpha)
    return int(piece.sum())


def seal_tail(layers, reference, core=2, notch=15, rim=3):
    """Makes the tail whole where it shows. The pieces it was stitched from (a recovered base, a piece from the hair)
    meet with soft edges and leave notches between them, which show the backdrop as a crack once the tail swings over
    it. Notches up to about 2 * `notch` px across (the tail's outline closed by a disk of radius `notch`) are filled
    with the art as it shows there, so the rest pose is unchanged; then the tail is opaque inside its outline (eroded
    by `core` px).
    What layers drawn over it hide keeps its opacity (See-Through's guess there would show as a slab once the tail
    swings out) but takes a blur of the tail that shows, `rim` px inside its outline, so it is no dark band either."""
    index = next((k for k, layer in enumerate(layers) if layer.tag == "tail"), None)
    if index is None:
        return
    tail = layers[index]
    hidden = coverage(layers[index + 1:]) > 0.5 if index + 1 < len(layers) else np.zeros(tail.alpha.shape, bool)
    solid = tail.alpha > 0.5
    yy, xx = np.mgrid[-notch:notch + 1, -notch:notch + 1]
    disk = xx * xx + yy * yy <= notch * notch
    closed = ndimage.binary_fill_holes(ndimage.binary_closing(solid, structure=disk))
    gap = closed & ~solid & ~hidden
    tail.rgba[..., :3] = np.where(gap[..., None], reference, tail.rgba[..., :3])
    tail.rgba[..., 3] = np.where(gap, 1.0, tail.alpha)
    inside = ndimage.binary_erosion(ndimage.binary_fill_holes(tail.alpha > 0.5), iterations=core)
    tail.rgba[..., 3] = np.where(inside & ~hidden, 1.0, tail.alpha)
    # What the clothes hide of the tail is See-Through's guess, often dark; it shows once the tail swings out, so it
    # takes a blur of the tail that shows, away from its outline.
    covered = hidden & (tail.alpha > 0.05)
    shows = ndimage.binary_erosion((tail.alpha > 0.5) & ~hidden, iterations=rim)
    if covered.any() and shows.any():
        ys, xs = np.nonzero(covered | shows)
        h, w = covered.shape
        box = (slice(max(ys.min() - 4, 0), min(ys.max() + 5, h)), slice(max(xs.min() - 4, 0), min(xs.max() + 5, w)))
        view = tail.rgba[box]
        colors = _smooth_fill(view[..., :3].copy(), shows[box])
        view[..., :3] = np.where(covered[box][..., None], colors, view[..., :3])


def tail_alone(layers, reference, share=0.5, min_px=2000):
    """Leaves the tail the only drawing of the tail. The split also paints the tail into the layers around it: a
    tail hanging in front of the skirt goes behind the skirt with a copy of it in the skirt, and a tail curling over
    the hair leaves a copy in the back hair; either copy shows once the tail swings. A clothing layer
    (`FRONT_OF_TAIL`) drawn over the tail is in front of the tail only if the art shows the skirt there: where at least
    `share` of their overlap (`min_px` or more) the tail layer's own paint matches the art, the tail is what shows, so
    the tail moves to just above those layers and their copy is repainted. A layer drawn under the tail whose paint
    matches the tail's on at least `share` of their overlap holds a copy too, which is repainted (_repaint_copy).
    Returns the layers and the names of the layers whose copy was repainted."""
    index = next((k for k, layer in enumerate(layers) if layer.tag == "tail"), None)
    if index is None:
        return layers, []
    tail = layers[index]
    mask = tail.alpha > 0.5
    shown = ndimage.uniform_filter(np.abs(tail.rgba[..., :3] - reference).mean(axis=2), 5) < 0.06
    covered, repainted = [], []
    for k, layer in enumerate(layers):
        if k == index:
            continue
        overlap = mask & (layer.alpha > 0.5)
        if overlap.sum() < min_px:
            continue
        if k > index and layer.tag in FRONT_OF_TAIL and (shown & overlap).sum() >= share * overlap.sum():
            covered.append(k)
            cleared = _repaint_copy(layer, ndimage.binary_dilation(mask, iterations=2) & (layer.alpha > 0.02), mask)
            tail.rgba[..., 3] = np.where(cleared, 1.0, tail.alpha)
            repainted.append(layer.name)
        elif k < index:
            same = ndimage.uniform_filter(np.abs(layer.rgba[..., :3] - tail.rgba[..., :3]).mean(axis=2), 5) < 0.06
            if (same & overlap).sum() >= share * overlap.sum():
                # Everything the tail covers, with its ink outline, which matches the tail's paint less.
                copy = (tail.alpha > 0.05) & (layer.alpha > 0.02)
                _repaint_copy(layer, copy, copy)
                repainted.append(layer.name)
    if covered:
        layers = [layer for k, layer in enumerate(layers) if k != index]
        layers.insert(max(covered), tail)
    return layers, repainted


def drop_empty(layers, threshold=0.12):
    """Layers left with nothing solid once the art is painted onto its owners (a faint nose the face covers) or split
    by the arms (a sleeve whose other half went with a held doll)."""
    return [layer for layer in layers if (layer.alpha > threshold).any()]


def split_pairs(layers, center_x, min_share=0.03):
    """Splits paired parts (arms, eyes, ears) into image-left and image-right layers. A paired part found on one side
    only (the open eye of a wink) takes that side, so it follows that eye; arms left whole go to split_by_arms."""
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
            if layer.tag == "handwear":
                out.append(layer)
            else:
                side = "left" if left_ids else "right"
                out.append(Layer(f"{layer.name}.{side}", layer.tag, layer.rgba, side))
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
    the art's ink lines: the arm grows from its bones (pixels within `core` px of shoulder, elbow, wrist and the hand,
    0.6 forearms past the wrist as the rig's arm tip), the body from the torso (between the shoulders, `inset` face
    widths in from each, below the upper face, over `clear` face widths from the arm's bones so a forearm or a hand
    across the chest seeds no body), over the art's gradient (a watershed), so a sleeve stays with its arm up to the
    line that parts it from the robe. What the body reaches becomes a `topwear` layer right after the arm, so it keeps
    its place in the draw order and moves with the chest and hips. Returns the layers and the pixels moved."""
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
        tip = wrist + 0.6 * (wrist - elbow)
        bone = np.minimum.reduce([_segment_distance(xx, yy, a, b)
                                  for a, b in ((shoulder, elbow), (elbow, wrist), (wrist, tip))])
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


def fill_hidden_legs(layers, solid=0.3, shade=10, edge=2, reach=6):
    """Repaints the legs the clothes above hide (the thighs under a skirt) from the legs that show: See-Through's
    guess there is faint and edged in grey, and a hem that swings up shows it as a second pair of legs. Every hidden
    leg pixel takes the color of the nearest leg pixel that shows (over `shade` px below the clothes, clear of the
    shadow they cast, and `edge` px inside the outline), and is opaque where the guess is over `solid` (ramping to it
    below), so the leg runs on up under the skirt in its own colors. Only what lies within a 45-degree cone above or
    below that pixel (`reach` px wider) stays: a leg runs on up, a blocky guess beside it goes. Returns the pixels
    repainted."""
    repainted = 0
    for i, leg in enumerate(layers):
        if leg.tag != "legwear" or i == len(layers) - 1:
            continue
        above = coverage(layers[i + 1:]) > 0.5
        shows = (leg.alpha > 0.5) & ~above
        hidden = (leg.alpha > 0.02) & above
        if not shows.any() or not hidden.any():
            continue
        source = ndimage.binary_erosion(shows, iterations=edge) & ~ndimage.binary_dilation(above, iterations=shade)
        if not source.any():
            source = shows
        _, (iy, ix) = ndimage.distance_transform_edt(~source, return_indices=True)
        yy, xx = np.mgrid[0:leg.alpha.shape[0], 0:leg.alpha.shape[1]]
        along = np.abs(xx - ix) <= np.abs(yy - iy) + reach
        leg.rgba[..., :3] = np.where(hidden[..., None], leg.rgba[iy, ix, :3], leg.rgba[..., :3])
        leg.rgba[..., 3] = np.where(hidden, np.where(along, np.clip(leg.alpha / solid, 0, 1), 0.0), leg.alpha)
        repainted += int(hidden.sum())
    return repainted


def back_tail_root(layers, root, reach, band=25, rim=3, pad=4):
    """Fills what lies behind the tail near its root (within `reach` px) with the layers drawn under it (not wings,
    which swing too), each running on up to `band` px past its own pixels, the nearest layer first, in a smooth blur
    of its colors away from its outline (_smooth_fill of its pixels `rim` px inside; copying its nearest pixel would
    streak its ink lines): the tail swings away from the body there, and what it uncovers shows the coat or the hair
    it hung over, not the backdrop. Returns the pixels filled."""
    index = next((k for k, layer in enumerate(layers) if layer.tag == "tail"), None)
    if index is None or index == 0:
        return 0
    tail = layers[index]
    below = [layer for layer in layers[:index] if layer.tag != "wings"]
    if not below:
        return 0
    h, w = tail.alpha.shape
    yy, xx = np.mgrid[0:h, 0:w]
    zone = (tail.alpha > 0.05) & (np.hypot(xx - root[0], yy - root[1]) < reach) & (coverage(layers[:index]) < 0.98)
    if not zone.any():
        return 0
    ys, xs = np.nonzero(zone)
    r = int(reach) + pad
    box = (slice(max(ys.min() - r, 0), min(ys.max() + r, h)), slice(max(xs.min() - r, 0), min(xs.max() + r, w)))
    best = np.full(zone[box].shape, np.inf)
    owner = np.full(zone[box].shape, -1)
    for k, layer in enumerate(below):
        solid = layer.alpha[box] > 0.5
        if not solid.any():
            continue
        dist = ndimage.distance_transform_edt(~solid)
        closer = dist < best
        best = np.where(closer, dist, best)
        owner = np.where(closer, k, owner)
    filled = 0
    for k, layer in enumerate(below):
        pick = zone[box] & (owner == k) & (best < band)
        if not pick.any():
            continue
        view = layer.rgba[box]
        known = ndimage.binary_erosion(view[..., 3] > 0.5, iterations=rim)
        colors = _smooth_fill(view[..., :3].copy(), known if known.any() else view[..., 3] > 0.5)
        view[..., :3] = np.where(pick[..., None], colors, view[..., :3])
        view[..., 3] = np.where(pick, 1.0, view[..., 3])
        filled += int(pick.sum())
    return filled


def grow_underlays(layers, radius=10, seam=3):
    """Extends each layer under the opaque layers above it, so moving parts never open gaps. Under a tail or wings
    only by `seam` px, enough to back their soft edge: they swing far off, and more would show as fragments of the
    layer's edge."""
    for i, layer in enumerate(layers):
        if layer.tag in NO_GROWTH or i == len(layers) - 1:
            continue
        above = coverage(layers[i + 1:]) > 0.98
        steady = coverage([upper for upper in layers[i + 1:] if upper.tag not in ("tail", "wings")]) > 0.98
        solid = layer.alpha > 0.5
        if not solid.any():
            continue
        grow = ndimage.binary_dilation(solid, iterations=radius) & above & ~solid
        grow &= steady | ndimage.binary_dilation(solid, iterations=seam)
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
