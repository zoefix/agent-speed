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
# With a static object (a wheelchair) only the arms hide what a gesture reveals: the object stays put in every pose.
ARM_TAGS = ("handwear", "hand")


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


def lift_hats(layers, face_box, above=0.3, min_px=1000, overlap=0.35, halo_fill=0.25):
    """Gives the head what the split drew of it into the top: topwear above the upper `above` of the face (a white
    beret the split takes for the white blouse) becomes a `headwear` layer named `hat`, so it turns with the head.
    It keeps the top's place in the draw order while it sits on the head; a hat the art shows tossed above the head
    (the median gap between its lower edge and the head's top, column by column, over a tenth of its height; a stray
    strand of hair may still touch it) is set down on it, its lower `overlap` over the head's top, and drawn over the
    hair. A halo (its pixels over opacity 0.5 fill under `halo_fill` of its box: a ring, a crown of thorns) floats
    above the head in every pose and stays where the art draws it.
    Returns the layers, how many hats it lifted and the pixels a hat set down left or covered, which the rest check
    leaves out."""
    fx0, fy0, fx1, fy1 = face_box
    cut = int(fy0 + above * (fy1 - fy0))
    head = np.zeros(layers[0].alpha.shape, bool)
    for layer in layers:
        if layer.tag in HEAD_TAGS:
            head |= layer.alpha > 0.5
    out, seated, lifted = [], [], 0
    shifted = np.zeros(head.shape, bool)
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
        halo = (rgba[..., 3] > 0.5).sum() < halo_fill * (height + 1) * (xs.max() - xs.min() + 1)
        if halo or not gaps or np.median(gaps) <= 0.1 * height:
            out.append(Layer("hat", "headwear", rgba))
            continue
        dy = int(np.median(gaps) + overlap * height)
        moved = np.zeros_like(rgba)
        moved[dy:] = rgba[:rgba.shape[0] - dy]
        seated.append(Layer("hat", "headwear", moved))
        shifted |= hat | (moved[..., 3] > 0.02)
    return out + seated, lifted, shifted


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


RIM_TAGS = ("handwear", "hand", "objects", "front hair", "tail", "wings")


def unmix_rims(layers, reference, owner, moving=RIM_TAGS, width=2, min_alpha=0.08, flat=0.01):
    """Gives each moving part the soft rim of its outline that reproject left on the layers under it (`owner`): the
    art's anti-aliased edge pixels go to the layer below once a part covers them by less than half, so a lock of hair or
    an arm moving away leaves a dotted copy of its outline. Within `width` px of the part, a pixel a lower layer owns
    splits into the part's nearest color at the share the art implies, over that layer's own color around it (a blur
    of its pixels farther out); `flat` is the least squared color distance between them that tells a rim apart. The
    rest pose stays the same. Returns the pixels split."""
    h, w = owner.shape
    total = 0
    for m, part in enumerate(layers):
        if part.tag not in moving:
            continue
        solid = owner == m
        if not solid.any():
            continue
        ring = ndimage.binary_dilation(solid, iterations=width) & ~solid & (owner >= 0) & (owner < m)
        if not ring.any():
            continue
        ys, xs = np.nonzero(ring)
        pad = 8 * width
        box = (slice(max(ys.min() - pad, 0), min(ys.max() + pad + 1, h)),
               slice(max(xs.min() - pad, 0), min(xs.max() + pad + 1, w)))
        near = ndimage.binary_dilation(solid[box], iterations=width + 1)
        _, (iy, ix) = ndimage.distance_transform_edt(~solid[box], return_indices=True)
        edge = part.rgba[box][..., :3][iy, ix]
        ref = reference[box]
        for j in np.unique(owner[box][ring[box]]):
            lower = layers[j]
            mine = ring[box] & (owner[box] == j)
            known = (owner[box] == j) & ~near
            if known.sum() < 10:
                continue
            own = _smooth_fill(lower.rgba[box][..., :3], known)
            d = edge - own
            dd = (d * d).sum(axis=2)
            share = np.clip(((ref - own) * d).sum(axis=2) / np.maximum(dd, 1e-6), 0, 1)
            take = mine & (dd > flat) & (share > min_alpha)
            if not take.any():
                continue
            view, under = part.rgba[box], lower.rgba[box]
            view[..., :3] = np.where(take[..., None], edge, view[..., :3])
            view[..., 3] = np.where(take, share, view[..., 3])
            under[..., :3] = np.where(take[..., None], own, under[..., :3])
            total += int(take.sum())
    return total


def _own_paint(layer, reference, tol=0.06):
    """Where a layer's own paint matches the art (mean channel difference over a 5 px box under `tol`)."""
    return ndimage.uniform_filter(np.abs(layer.rgba[..., :3] - reference).mean(axis=2), 5) < tol


def _settle(layer, shown, hidden, apart):
    """Repaints the `hidden` pixels whose paint stands apart from a blur of the colors the layer shows."""
    if not hidden.any() or shown.sum() < 50:
        return 0
    expected = _smooth_fill(layer.rgba[..., :3], shown)
    off = hidden & (np.abs(layer.rgba[..., :3] - expected).mean(axis=2) > apart)
    layer.rgba[..., :3] = np.where(off[..., None], expected, layer.rgba[..., :3])
    return int(off.sum())


def clean_hidden(layers, owner, apart=0.3, skip=NO_GROWTH):
    """Settles what See-Through guessed behind the layers over each layer (`owner`, from reproject): a guess that
    stands apart from the layer's own colors the art shows around it (mean channel difference over `apart` from a
    blur of them: a dark slab on a white blouse, a hand drawn into the book under it) takes that blur, so a part that
    moves away uncovers the layer's own colors. Returns the pixels repainted."""
    total = 0
    for i, layer in enumerate(layers):
        if layer.tag not in skip:
            total += _settle(layer, owner == i, (layer.alpha > 0.5) & (owner != i), apart)
    return total


def objects_over_arms(layers, reference, margin=0.05, min_px=300):
    """Takes out of the arms what the art shows of a held object over them (a book held against a sleeve: the split
    draws the arms above the objects): where an object's paint is nearer the art than the paint of an arm layer drawn
    above it (by `margin` in mean channel difference over a 5 px box; See-Through's object paint is near, not equal),
    in pieces of at least `min_px`, the arm leaves the pixels to the object, so the art there is painted on the object.
    Returns what it took, for arms_behind_objects to give back as the part of the arm behind the object once the arms
    are split."""
    taken = []
    for io, obj in enumerate(layers):
        if obj.tag != "objects":
            continue
        d_obj = None
        for arm in layers[io + 1:]:
            if arm.tag not in ARM_TAGS:
                continue
            overlap = (obj.alpha > 0.5) & (arm.alpha > 0.5)
            if overlap.sum() < min_px:
                continue
            if d_obj is None:
                d_obj = ndimage.uniform_filter(np.abs(obj.rgba[..., :3] - reference).mean(axis=2), 5)
            d_arm = ndimage.uniform_filter(np.abs(arm.rgba[..., :3] - reference).mean(axis=2), 5)
            wins = ndimage.binary_opening(overlap & (d_obj + margin < d_arm), iterations=2)
            wins = ndimage.binary_closing(wins, iterations=3) & overlap
            labels, count = ndimage.label(wins)
            if not count:
                continue
            sizes = np.asarray(ndimage.sum(wins, labels, range(1, count + 1)))
            front = np.isin(labels, 1 + np.flatnonzero(sizes >= min_px))
            if front.any():
                behind = arm.rgba.copy()
                behind[..., 3] = np.where(front, arm.alpha, 0.0)
                arm.rgba[..., 3] = np.where(front, 0.0, arm.alpha)
                taken.append((arm.tag, behind))
    return taken


FACE_TAGS = ("face", "eyewhite", "irides", "eyelash", "eyebrow", "nose", "mouth")


def _over_face(layer, heads, reference, min_px, off, margin):
    """Pieces (at least `min_px`) where `layer` and the face's layers `heads` overlap and the art shows `layer`: the
    face's own paint there is off the art (median of the mean channel difference over a 5 px box above `off`:
    something is drawn over it) and the layer's is no further off by `margin`. Returns (overlap, pieces)."""
    head = coverage(heads)
    overlap = (layer.alpha > 0.5) & (head > 0.5)
    front = np.zeros_like(overlap)
    if overlap.sum() < min_px:
        return overlap, front
    drawn = composite(heads, background=0.0) / np.maximum(head, 1e-6)[..., None]
    d_head = ndimage.uniform_filter(np.abs(drawn - reference).mean(axis=2), 5)
    d_layer = ndimage.uniform_filter(np.abs(layer.rgba[..., :3] - reference).mean(axis=2), 5)
    labels, count = ndimage.label(overlap)
    for k in range(1, count + 1):
        piece = labels == k
        if piece.sum() < min_px:
            continue
        face_off, layer_off = float(np.median(d_head[piece])), float(np.median(d_layer[piece]))
        if face_off > off and layer_off < face_off + margin:
            front |= piece
    return overlap, front


def arms_over_face(layers, reference, min_px=30, off=0.06, margin=0.05, bridge=8):
    """Draws an arm the art shows over the face (a finger on the lips, _over_face) above the face: the split draws the
    face over the arms, so reproject painted the fingertip onto the face, where it stayed when the arm moved. Such an
    arm goes right above the face's features (below the bangs) and keeps none of the face's other pixels; reproject
    then paints the art there on the arm and the face's pixels under it are hidden, as far as the face closes around
    them (a finger crossing the jaw takes along what lies below it). Returns the layers and the pixels the arms
    took."""
    taken = 0
    for arm in [layer for layer in layers if layer.tag in ARM_TAGS]:
        heads = [layer for layer in layers[layers.index(arm) + 1:] if layer.tag in FACE_TAGS]
        if not heads:
            continue
        overlap, front = _over_face(arm, heads, reference, min_px, off, margin)
        if not front.any():
            continue
        arm.rgba[..., 3] = np.where(overlap & ~front, 0.0, arm.alpha)
        # What the face's layers drew under the arm past the face's own outline (the finger below the jaw) goes: the
        # face keeps it only where its other pixels (slivers of 2 px left out) close around it within `bridge` px.
        for h in heads:
            solid = h.alpha > 0.5
            if not (solid & front).any():
                continue
            body = ndimage.binary_opening(solid & ~front, iterations=2)
            keep = ndimage.binary_fill_holes(ndimage.binary_closing(body, iterations=bridge))
            h.rgba[..., 3] = np.where(front & solid & ~keep, 0.0, h.alpha)
        layers.remove(arm)
        top = max(i for i, layer in enumerate(layers) if layer.tag in FACE_TAGS)
        layers.insert(top + 1, arm)
        taken += int(front.sum())
    return layers, taken


def earrings_over_hair(layers, reference, min_px=30, margin=0.02, share=0.6):
    """Draws an earring the art shows over the side locks above the front hair: See-Through paints it into the earwear
    and copies it, roughly, into the front hair over it, and a turn of the head moves the two apart (a doubled
    earring). When the earwear's paint is as near the art as the hair's (median mean channel difference, within
    `margin`) on pieces of its overlap with the front hair holding `share` of it, at least `min_px`, it goes right
    above the front hair and keeps none of the other pieces, where the art shows the hair; the copy in the hair is
    then hidden, and repainted. Returns the layers and the pixels moved."""
    moved = 0
    for ear in [layer for layer in layers if layer.tag == "earwear"]:
        hairs = [layer for layer in layers[layers.index(ear) + 1:] if layer.tag == "front hair"]
        if not hairs:
            continue
        overlap = (ear.alpha > 0.5) & (coverage(hairs) > 0.5)
        if overlap.sum() < min_px:
            continue
        hair = coverage(hairs)
        ear_off = np.abs(ear.rgba[..., :3] - reference).mean(axis=2)
        hair_off = np.abs(composite(hairs, background=0.0) / np.maximum(hair, 1e-6)[..., None] - reference).mean(axis=2)
        labels, count = ndimage.label(overlap, structure=np.ones((3, 3)))
        front = np.isin(labels, [k for k in range(1, count + 1)
                                 if np.median(ear_off[labels == k]) <= np.median(hair_off[labels == k]) + margin])
        if front.sum() < share * overlap.sum():
            continue
        ear.rgba[..., 3] = np.where(overlap & ~front, 0.0, ear.alpha)
        layers.remove(ear)
        layers.insert(max(layers.index(hair) for hair in hairs) + 1, ear)
        moved += int(front.sum())
    return layers, moved


def features_off_face(layers, face_box, margin=0.25, enclosed=0.5):
    """Gives eyes, brows, a nose or a mouth drawn away from the face (a plush toy's face in her arms) to the layer
    they lie on: See-Through files them with the face's features, which blink, follow the head and fade under face
    patches, so they floated where the toy had been once it moved. A piece of a feature layer (8-connected, alpha
    over 0.3, grown 2 px over its soft edge) wholly outside the face's box grown by `margin` of its width goes into
    the layer drawn below it (not the face's own layers or hair) that holds most of a 2 px ring around it, at least
    `enclosed` of it, when the layers between are clear there; it is composited over that layer, so the rest pose
    stays the same. Returns the pixels moved."""
    x0, y0, x1, y1 = face_box
    m = margin * (x1 - x0)
    near = np.zeros(layers[0].alpha.shape, bool)
    near[max(0, int(y0 - m)):int(y1 + m) + 1, max(0, int(x0 - m)):int(x1 + m) + 1] = True
    moved = 0
    for feature in [layer for layer in layers if layer.tag in FACE_TAGS and layer.tag != "face"]:
        below = [layer for layer in layers[:layers.index(feature)]
                 if layer.tag not in FACE_TAGS + ("front hair", "back hair")]
        if not below:
            continue
        labels, count = ndimage.label(feature.alpha > 0.3, structure=np.ones((3, 3)))
        for k in range(1, count + 1):
            core = labels == k
            if (core & near).any():
                continue
            piece = ndimage.binary_dilation(core, iterations=2) & (feature.alpha > 0) & ~near
            ring = ndimage.binary_dilation(piece, iterations=2) & ~piece
            shares = [float((layer.alpha[ring] > 0.5).mean()) for layer in below]
            if max(shares) < enclosed:
                continue
            target = below[int(np.argmax(shares))]
            between = layers[layers.index(target) + 1:layers.index(feature)]
            if between and float(coverage(between)[piece].max()) > 0.05:
                continue
            a, t = feature.alpha[piece][:, None], target.alpha[piece][:, None]
            total = a + t * (1 - a)
            color = (feature.rgba[piece][:, :3] * a + target.rgba[piece][:, :3] * t * (1 - a)) / np.maximum(total, 1e-6)
            target.rgba[piece] = np.concatenate([color, total], axis=1)
            feature.rgba[..., 3][piece] = 0.0
            moved += int(piece.sum())
    return moved


def objects_over_face(layers, min_px=30):
    """Where a held object overlaps the face (a flute's lip plate at the lips; See-Through often paints it into the
    face layer as well): an object is never behind the face, so the face keeps what the art shows there and face
    patches leave it as drawn (an edit that left the plate out would make it pop in and out). Pieces of at least
    `min_px`. Returns the mask."""
    out = np.zeros(layers[0].alpha.shape, bool)
    for obj in [layer for layer in layers if layer.tag == "objects"]:
        heads = [layer for layer in layers if layer.tag in FACE_TAGS]
        if not heads:
            continue
        overlap = (obj.alpha > 0.5) & (coverage(heads) > 0.5)
        labels, count = ndimage.label(overlap)
        if count:
            sizes = np.asarray(ndimage.sum(overlap, labels, range(1, count + 1)))
            out |= np.isin(labels, 1 + np.flatnonzero(sizes >= min_px))
    return out


def features_into_face(layers, keep, reach=1):
    """Gives the face layer what the features (eyes, brows, nose, mouth) draw where the art shows an object over the
    face (`keep`, from objects_over_face, grown by `reach` px): See-Through often paints a flute's lip plate into the
    mouth, and a face patch fades the features out but leaves the face as it is there. The rest pose stays the same.
    Returns the pixels moved."""
    face = next((layer for layer in layers if layer.tag == "face"), None)
    if face is None or not keep.any():
        return 0
    features = [layer for layer in layers[layers.index(face) + 1:] if layer.tag in FACE_TAGS and layer.tag != "face"]
    area = ndimage.binary_dilation(keep, iterations=reach)
    area &= coverage(features) > 0 if features else False
    if not area.any():
        return 0
    ys, xs = np.nonzero(area)
    box = (slice(ys.min(), ys.max() + 1), slice(xs.min(), xs.max() + 1))
    color, cover = _over([face] + features, box)
    here = area[box]
    straight = color / np.maximum(cover, 1e-6)[..., None]
    face.rgba[box][here] = np.concatenate([straight, cover[..., None]], axis=-1)[here]
    for layer in features:
        layer.rgba[box][..., 3][here] = 0.0
    return int(here.sum())


def arms_behind_objects(layers, taken, min_px=300, apart=0.3):
    """Gives each split arm what objects_over_arms took near it (each pixel to the nearest arm of that tag), its paint
    settled against the arm's own colors (_settle), and draws the objects it lies under right above that arm. An arm
    the art also shows over the same object (at least `min_px` of it drawn over the object elsewhere) keeps the object
    under it and stays without those pixels. Returns the layers and the pixels given back."""
    given = 0
    for tag, behind in taken:
        mask = behind[..., 3] > 0.02
        arms = [layer for layer in layers if layer.tag == tag]
        if not arms or not mask.any():
            continue
        dist = np.stack([ndimage.distance_transform_edt(~(arm.alpha > 0.5)) for arm in arms])
        nearest = np.argmin(dist, axis=0)
        for k, arm in enumerate(arms):
            mine = mask & (nearest == k)
            if mine.sum() < min_px:
                continue
            objects = [layer for layer in layers if layer.tag == "objects" and (layer.alpha[mine] > 0.5).any()]
            if any(((obj.alpha > 0.5) & (arm.alpha > 0.5) & ~mine).sum() >= min_px for obj in objects):
                continue
            shown = arm.alpha > 0.5
            arm.rgba[..., :3] = np.where(mine[..., None], behind[..., :3], arm.rgba[..., :3])
            arm.rgba[..., 3] = np.where(mine, behind[..., 3], arm.alpha)
            _settle(arm, shown, mine, apart)
            given += int(mine.sum())
            for obj in objects:
                if layers.index(obj) < layers.index(arm):
                    layers.remove(obj)
                    layers.insert(layers.index(arm) + 1, obj)
    return layers, given


def tail_off_head(layers, face_box, bridge=3, reach=3):
    """Gives the hat what the split took for tail on it (a hat's white plume): each piece of the tail layer (pieces
    `bridge` px apart are one) that lies wholly above the chin and touches the headwear (within `reach` px) joins
    the headwear. A tail left with nothing goes. Returns the layers and the pixels moved."""
    tail = next((layer for layer in layers if layer.tag == "tail"), None)
    hat = next((layer for layer in layers if layer.tag == "headwear"), None)
    if tail is None or hat is None:
        return layers, 0
    mask = tail.alpha > 0.02
    labels, count = ndimage.label(ndimage.binary_closing(mask, iterations=bridge) | mask)
    if not count:
        return layers, 0
    near = ndimage.binary_dilation(hat.alpha > 0.5, iterations=reach)
    rows = np.broadcast_to(np.arange(mask.shape[0])[:, None], mask.shape)
    lowest = np.asarray(ndimage.maximum(rows, labels, range(1, count + 1)))
    touching = np.asarray(ndimage.maximum(near, labels, range(1, count + 1))) > 0
    piece = np.isin(labels, 1 + np.flatnonzero(touching & (lowest < face_box[3]))) & mask
    if not piece.any():
        return layers, 0
    take = piece & (tail.alpha > hat.alpha)
    hat.rgba[..., :3] = np.where(take[..., None], tail.rgba[..., :3], hat.rgba[..., :3])
    hat.rgba[..., 3] = np.where(piece, np.maximum(hat.alpha, tail.alpha), hat.alpha)
    tail.rgba[..., 3] = np.where(piece, 0.0, tail.alpha)
    if tail.alpha.max() < 0.12:
        layers = [layer for layer in layers if layer is not tail]
    return layers, int(piece.sum())


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


def seal_tail(layers, reference, core=2, notch=15, rim=3, paper=None):
    """Makes the tail whole where it shows. The pieces it was stitched from (a recovered base, a piece from the hair)
    meet with soft edges and leave notches between them, which show the backdrop as a crack once the tail swings over
    it. Notches up to about 2 * `notch` px across (the tail's outline closed by a disk of radius `notch`) are filled
    with the art as it shows there, so the rest pose is unchanged, except the bare `paper` (matte.paper) between the
    tufts of a furry outline, which would stay on the tail as white specks; then the tail is opaque inside its outline
    (eroded by `core` px).
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
    if paper is not None:
        gap &= ~paper
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


def wings_alone(layers, reach=6, island=0.05, enclosed=0.6):
    """Leaves the wings the only drawing of the wings: See-Through also paints their silhouette into a layer under them
    (the back hair holds a dark copy of both wings), which shows once they flap. A layer's pixels under the wings
    more than `reach` px from where that layer shows beside them go. A bit of that layer then cut off from the rest
    (seen through a gap between the wing's spars) would float as a dash once the wings flap: it goes into the wing
    framing it (_islands_to, the rest pose stays the same). Returns the pixels cleared."""
    cleared = 0
    wings = [layer for layer in layers if layer.tag == "wings"]
    for wing in wings:
        over = wing.alpha > 0.5
        for layer in layers[:layers.index(wing)]:
            solid = layer.alpha > 0.5
            hidden = solid & over
            if not hidden.any():
                continue
            far = hidden & (ndimage.distance_transform_edt(~(solid & ~over)) > reach)
            layer.rgba[..., 3] = np.where(far, 0.0, layer.alpha)
            cleared += int(far.sum())
            if far.any():
                _islands_to(layer, layers, [w for w in wings if layers.index(w) > layers.index(layer)], island,
                            enclosed)
    return cleared


# Layers that keep still over the back hair: what they hide of it shows only when the hair itself swings out.
BODY_TAGS = ("neck", "topwear", "neckwear", "bottomwear", "legwear", "footwear")


def back_hair_alone(layers, reach=10, cover=0.995, island=0.05, enclosed=0.6, moving_reach=20):
    """Leaves the back hair only near where it shows: See-Through paints a whole sheet of it behind the body (flat,
    with the gaps between curls drawn as outlines), and LaMa repaints it flat; once the hair swings out past the body
    that sheet shows as a slab. Back hair the body's clothes (`BODY_TAGS`) cover by `cover`, more than `reach` px from
    where the back hair shows, goes, fading out over the last half of that reach (no straight cut edge once it swings
    out): the swing uncovers the stage behind, as between the strands. What arms, hands, objects, the head or the
    front hair cover stays within `moving_reach` px, as far as their own small sways uncover it (a gesture shows the
    variant's art there instead), fading out likewise: a long sleeve held out hid a whole sheet of it, a grey ghost
    once the hair swung. A bit of it then cut off from the rest (under `island` of its
    largest piece), seen through a gap in the clothes that frame it (`enclosed` of its rim), would float once the hair
    swings: it goes into the clothing layer framing it most and keeps still with it (the rest pose stays the same).
    Returns the pixels cleared."""
    cleared = 0
    for hair in [layer for layer in layers if layer.tag == "back hair"]:
        above = layers[layers.index(hair) + 1:]
        body = [layer for layer in above if layer.tag in BODY_TAGS]
        if not body:
            continue
        solid = hair.alpha > 0.02
        shown = (hair.alpha > 0.5) & (coverage(above) < 0.5)
        if not shown.any():
            continue
        behind = solid & (coverage(body) > cover)
        under = solid & ~behind & (coverage(above) > cover)
        dist = ndimage.distance_transform_edt(~shown)
        fade = np.clip((reach - dist) / (reach / 2), 0, 1).astype(np.float32)
        far = np.clip((moving_reach - dist) / (moving_reach / 2), 0, 1).astype(np.float32)
        hair.rgba[..., 3] = np.where(behind, hair.alpha * fade, np.where(under, hair.alpha * far, hair.alpha))
        cleared += int((behind & (dist > reach)).sum() + (under & (dist > moving_reach)).sum())
        _islands_to(hair, layers, body, island, enclosed)
    return cleared


def _islands_to(hair, layers, frames, island, enclosed):
    """Gives each small piece of `hair` (under `island` of its largest piece) that `frames` (layers above it) frame by
    `enclosed` of its rim to the frame around it most, keeping the composite as it was."""
    body = frames
    if not body:
        return
    mass = hair.alpha > 0.5
    labels, count = ndimage.label(mass, structure=np.ones((3, 3)))
    if count < 2:
        return
    sizes = np.asarray(ndimage.sum(mass, labels, range(1, count + 1)))
    for k in 1 + np.flatnonzero(sizes < island * sizes.max()):
        core = labels == k
        piece = ndimage.binary_dilation(core, iterations=2) & (hair.alpha > 0) & ~(mass & ~core)
        ring = ndimage.binary_dilation(piece, iterations=2) & ~piece
        shares = [float((layer.alpha[ring] > 0.5).mean()) for layer in body]
        if max(shares) < enclosed:
            continue
        target = body[int(np.argmax(shares))]
        between = layers[layers.index(hair) + 1:layers.index(target)]
        if between and float(coverage(between)[piece].max()) > 0.05:
            continue
        a, t = hair.alpha[piece][:, None], target.alpha[piece][:, None]
        total = t + a * (1 - t)
        color = (target.rgba[piece][:, :3] * t + hair.rgba[piece][:, :3] * a * (1 - t)) / np.maximum(total, 1e-6)
        target.rgba[piece] = np.concatenate([color, total], axis=1)
        hair.rgba[..., 3][piece] = 0.0


def tail_alone(layers, reference, share=0.5, min_px=2000):
    """Leaves the tail the only drawing of the tail. The split also paints the tail into the layers around it: a
    tail hanging in front of the skirt goes behind the skirt with a copy of it in the skirt, and a tail curling over
    the hair leaves a copy in the back hair; either copy shows once the tail swings. A clothing layer
    (`FRONT_OF_TAIL`) drawn over the tail is in front of the tail only if the art shows the skirt there: where at least
    `share` of their overlap (`min_px` or more) the tail layer's own paint matches the art, the tail is what shows, so
    the tail moves to just above those layers and their copy is repainted. A layer drawn under the tail whose paint
    matches the tail's on at least `share` of their overlap holds a copy too, which is repainted (_repaint_copy).
    What the art shows of such a clothing layer over the tail (a coat's strap and tassel over the tail's root) stays in
    front of it as its own layer, `<name> front`, just above the tail, so the tail never carries it when it swings.
    Returns the layers and the names of the layers whose copy was repainted."""
    index = next((k for k, layer in enumerate(layers) if layer.tag == "tail"), None)
    if index is None:
        return layers, []
    tail = layers[index]
    mask = tail.alpha > 0.5
    shown = ndimage.uniform_filter(np.abs(tail.rgba[..., :3] - reference).mean(axis=2), 5) < 0.06
    inner = ndimage.binary_erosion(mask, iterations=4)
    covered, repainted, fronts = [], [], []
    expected = None
    for k, layer in enumerate(layers):
        if k == index:
            continue
        overlap = mask & (layer.alpha > 0.5)
        if overlap.sum() < min_px:
            continue
        if k > index and layer.tag in FRONT_OF_TAIL and (shown & overlap).sum() >= share * overlap.sum():
            covered.append(k)
            if expected is None:
                # The tail's colors as the art shows them around each pixel: its paint where hidden is a guess.
                expected = _smooth_fill(tail.rgba[..., :3].copy(), mask & shown)
            front = _clothes_in_front(layer, expected, reference, overlap & ~shown, inner)
            if front.any():
                rgba = layer.rgba.copy()
                rgba[..., 3] = np.where(front, rgba[..., 3], 0.0)
                fronts.append(Layer(f"{layer.name} front", layer.tag, rgba, layer.side))
            copy = ndimage.binary_dilation(mask, iterations=2) & (layer.alpha > 0.02) & ~front
            if copy.any():
                cleared = _repaint_copy(layer, copy, mask & ~front)
                tail.rgba[..., 3] = np.where(cleared, 1.0, tail.alpha)
            repainted.append(layer.name)
        elif k < index:
            same = ndimage.uniform_filter(np.abs(layer.rgba[..., :3] - tail.rgba[..., :3]).mean(axis=2), 5) < 0.06
            if (same & overlap).sum() >= share * overlap.sum():
                # Everything the tail covers, with its ink outline, which matches the tail's paint less.
                copy = (tail.alpha > 0.05) & (layer.alpha > 0.02)
                _repaint_copy(layer, copy, copy)
                repainted.append(layer.name)
    if fronts:
        # Under what stays in front, the tail's paint is a guess; it takes the tail around it that the art shows.
        under = np.logical_or.reduce([f.alpha > 0.02 for f in fronts]) & (tail.alpha > 0.05)
        tail.rgba[..., :3] = np.where(under[..., None], expected, tail.rgba[..., :3])
    if covered:
        layers = [layer for k, layer in enumerate(layers) if k != index]
        at = max(covered)
        layers[at:at] = [tail] + fronts
    return layers, repainted


def _clothes_in_front(layer, tail_colors, reference, candidates, inner, min_px=40, reach=12, grow=2, apart=0.15):
    """The pixels of `layer` the art shows in front of the tail (a coat's strap and tassel over the tail's root).
    Seeds: among `candidates` (where the tail's paint does not match the art) within `inner` (the tail 4 px in from
    its outline, where the 5 px boxes never straddle it), those where the layer's own paint matches the art and
    stands apart from `tail_colors` (the tail's colors that the art shows around there; mean channel difference over
    `apart`: a white skirt by a white tail tip is no seed), in pieces of at least `min_px`. Each runs on up to `reach`
    px over the candidates the layer's paint matches (the rest of a tassel whose dark cord looks like fur), then
    `grow` px over the layer for its rim."""
    own = ndimage.uniform_filter(np.abs(layer.rgba[..., :3] - reference).mean(axis=2), 5) < 0.06
    unlike = ndimage.uniform_filter(np.abs(layer.rgba[..., :3] - tail_colors).mean(axis=2), 5) > apart
    allowed = candidates & own & (layer.alpha > 0.5)
    seeds = allowed & unlike & inner
    labels, count = ndimage.label(seeds)
    if not count:
        return seeds
    sizes = np.asarray(ndimage.sum(seeds, labels, range(1, count + 1)))
    seeds = np.isin(labels, 1 + np.flatnonzero(sizes >= min_px))
    front = ndimage.binary_dilation(seeds, iterations=reach, mask=allowed) | seeds
    return ndimage.binary_dilation(front, iterations=grow) & (layer.alpha > 0.02)


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


EYE_TAGS = ("eyewhite", "irides", "eyelash")
# Name suffix of the lash tips lashes_off_hair lifts out of the hair, above it.
TIP = ".tip"


def lashes_off_hair(layers, reach=4, dark=0.4, apart=0.2, local=21, island=80, faint=0.05, ring_alpha=0.5, beyond=12,
                    lash_share=0.75):
    """Takes out of the front hair what it drew of the eyes: See-Through puts a lash seen through the bangs (or drawn
    over them, as a winged liner) into the hair, and a face patch lies under the hair, so a closed or lowered eye left
    that piece of lash on the hair.

    A hair pixel (opaque over `faint`) over an eye's layers (grown by `reach` px) that is dark (luminance under
    `dark`) and darker than the hair around it (the median of its opaque pixels in a `local` px square, wide enough
    that the lash is not most of it) by `apart` is a lash when its dark stroke (8-connected, followed up to `beyond`
    px past the eyes) lies over the eyes by `lash_share` at least: the outline of a strand runs on past them and stays.
    Those pixels, grown by 1 px, take a blur of the hair's other colors (its dark strokes left out, so no grey smear;
    LaMa drew the strand's body color down into pale tips). What the art showed there goes to a tip layer of the
    nearest lash, right above the hair: opaque on the lash pixels, at least `ring_alpha` on the 1 px around them,
    colored so the rest pose stays the same (only where the layers up to the hair cover the pixel fully). The tip is
    an eyelash layer, so it follows the eye and fades out with the lashes under a face patch, leaving the bangs.

    A piece of the hair under `island` px lying over an eye goes to the nearest lash layer as it is. Returns the pixels
    taken or moved."""
    hair = [layer for layer in layers if layer.tag == "front hair"]
    eyes = [layer for layer in layers if layer.tag in EYE_TAGS]
    if not hair or not eyes:
        return 0
    eye = ndimage.binary_dilation(coverage(eyes) > 0.3, iterations=reach)
    lashes = [layer for layer in eyes if layer.tag == "eyelash"] or eyes
    nearest = np.argmin(np.stack([ndimage.distance_transform_edt(~(lash.alpha > 0.3)) for lash in lashes]), axis=0)
    tips = [Layer(lash.name + TIP, lash.tag, np.zeros_like(lash.rgba), lash.side) for lash in lashes]
    repainted = 0
    for h in hair:
        solid = h.alpha > faint
        # A speck of the hair alone over an eye (a lash tip the split cut loose into the hair) goes to the nearest
        # lash layer as it is: at rest it looks the same, and a face patch replaces it with the lashes.
        labels, count = ndimage.label(solid)
        if count:
            sizes = np.asarray(ndimage.sum(solid, labels, range(1, count + 1)))
            inside = np.asarray(ndimage.mean(eye, labels, range(1, count + 1)))
            specks = np.isin(labels, 1 + np.flatnonzero((sizes < island) & (inside > 0.9)))
            if specks.any():
                a = h.alpha
                for k, lash in enumerate(lashes):
                    mine = specks & (nearest == k)
                    under = lash.alpha
                    total = a + under * (1 - a)
                    mixed = (h.rgba[..., :3] * a[..., None] + lash.rgba[..., :3] * (under * (1 - a))[..., None])
                    lash.rgba[..., :3] = np.where(mine[..., None], mixed / np.maximum(total, 1e-6)[..., None],
                                                  lash.rgba[..., :3])
                    lash.rgba[..., 3] = np.where(mine, total, under)
                h.rgba[..., 3] = np.where(specks, 0.0, a)
                solid &= ~specks
                repainted += int(specks.sum())
        near = eye & solid
        if not near.any():
            continue
        luma = h.rgba[..., :3] @ np.array([0.299, 0.587, 0.114], np.float32)
        ys, xs = np.nonzero(near)
        pad = 2 * local + beyond
        box = (slice(max(ys.min() - pad, 0), ys.max() + pad + 1), slice(max(xs.min() - pad, 0), xs.max() + pad + 1))
        shade = luma[box]
        around = np.where((h.alpha > 0.5)[box], shade, np.nan)
        strokes = ndimage.binary_dilation(eye[box], iterations=beyond) & solid[box] & (shade < dark)
        r = local // 2
        for y, x in zip(*np.nonzero(strokes)):
            window = around[max(y - r, 0):y + r + 1, max(x - r, 0):x + r + 1]
            window = window[~np.isnan(window)]
            strokes[y, x] = window.size > 0 and shade[y, x] < np.median(window) - apart
        labels, count = ndimage.label(strokes, structure=np.ones((3, 3)))
        if not count:
            continue
        share = np.asarray(ndimage.mean(eye[box], labels, range(1, count + 1)))
        core = np.isin(labels, 1 + np.flatnonzero(share >= lash_share)) & near[box]
        if not core.any():
            continue
        upto = layers[:layers.index(h) + 1]
        shown, cover = _over(upto, box)
        # Only where the layers up to the hair cover the pixel fully can a tip over it keep the rest pose.
        take = ndimage.binary_dilation(core, iterations=1) & solid[box] & (cover > 0.999)
        core &= take
        view = h.rgba[box]
        colors = _smooth_fill(view[..., :3].copy(), solid[box] & ~take & ~strokes)
        view[..., :3] = np.where(take[..., None], colors, view[..., :3])
        repainted += int(take.sum())
        below, _ = _over(upto, box)
        # On the ring the tip is as clear as `ring_alpha` allows while a color in 0..1 still gives the art back.
        need = np.maximum(1 - shown / np.maximum(below, 1e-6), (shown - below) / np.maximum(1 - below, 1e-6)).max(-1)
        t = np.where(core, 1.0, np.clip(np.maximum(ring_alpha, need), 0, 1)).astype(np.float32) * take
        color = np.clip((shown - below * (1 - t)[..., None]) / np.maximum(t, 1e-6)[..., None], 0, 1)
        for k, tip in enumerate(tips):
            mine = (t > 0) & (nearest[box] == k)
            tip.rgba[box][mine] = np.concatenate([color, t[..., None]], axis=-1)[mine]
    at = max(layers.index(h) for h in hair) + 1
    layers[at:at] = [tip for tip in tips if tip.alpha.max() > 0]
    return repainted


def _over(layers, box):
    """The premultiplied color and the coverage of `layers` over nothing, within `box`."""
    color = np.zeros(layers[0].rgba[box].shape[:2] + (3,), np.float32)
    cover = np.zeros(color.shape[:2], np.float32)
    for layer in layers:
        a = layer.alpha[box]
        color = layer.rgba[box][..., :3] * a[..., None] + color * (1 - a)[..., None]
        cover = a + cover * (1 - a)
    return color, cover


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


# Layers drawn behind the body: a gap in the body over them is still a gap in the body.
BEHIND_TAGS = ("back hair", "tail", "wings")


def grow_underlays(layers, radius=10, seam=3, swinging=RIM_TAGS, swaying=BEHIND_TAGS):
    """Extends each layer under the opaque layers above it, so the seams between still parts never open. Under parts
    that swing away (arms, hands, objects, the bangs and locks, tails, wings) only by `seam` px, enough to back their
    soft edge: more would show as a tab of the layer past its outline once the part moved (the face under a finger
    below the jaw); a real gap under them is fill_enclosed_under's. Layers that sway on their own behind the body
    (back hair, tails, wings) grow only by `seam` px under anything: the rest swings out past the body's outline as
    a flat band around the hair."""
    for i, layer in enumerate(layers):
        if layer.tag in NO_GROWTH or i == len(layers) - 1:
            continue
        above = coverage(layers[i + 1:]) > 0.98
        steady = coverage([upper for upper in layers[i + 1:] if upper.tag not in swinging]) > 0.98
        solid = layer.alpha > 0.5
        if not solid.any():
            continue
        grow = ndimage.binary_dilation(solid, iterations=seam if layer.tag in swaying else radius) & above & ~solid
        grow &= steady | ndimage.binary_dilation(solid, iterations=seam)
        if not grow.any():
            continue
        _, (iy, ix) = ndimage.distance_transform_edt(~solid, return_indices=True)
        layer.rgba[..., :3] = np.where(grow[..., None], layer.rgba[iy, ix, :3], layer.rgba[..., :3])
        layer.rgba[..., 3] = np.where(grow, 1.0, layer.alpha)


MOVING_TAGS = ("handwear", "hand", "objects", "front hair", "tail")


def _between(mask, reach):
    """Pixels with `mask` within `reach` px on both sides along a row or along a column."""
    def sides(m):
        idx = np.arange(m.shape[1], dtype=np.float32)[None, :]
        left = np.maximum.accumulate(np.where(m, idx, -np.inf), axis=1)
        right = np.flip(np.minimum.accumulate(np.flip(np.where(m, idx, np.inf), axis=1), axis=1), axis=1)
        return (idx - left <= reach) & (right - idx <= reach)
    return sides(mask) | sides(mask.T).T


def fill_enclosed_under(layers, moving=MOVING_TAGS, min_px=30, pad=70, body_share=0.6, reach=60):
    """Fills the gaps the layers under a moving part leave inside themselves where that part covers them (a sleeve
    hanging over a torso the split left open, a braid over a blouse open down to the hair behind): each gap of at
    least `min_px` that the layers drawn below the part enclose, or that the body's layers (all but BEHIND_TAGS) hold
    `body_share` of the rim of (its pixels with the body within `reach` px to both sides, so nothing grows past the
    body's outline), joins the layer that holds most of its rim (a body layer for the latter), in a blur of that
    layer's colors (_smooth_fill), so the part moving away uncovers that layer instead of the background or the back
    hair. Returns the pixels filled."""
    filled = 0
    for m, part in enumerate(layers):
        if part.tag not in moving or m == 0:
            continue
        lower = coverage(layers[:m]) > 0.5
        body_ids = [i for i in range(m) if layers[i].tag not in BEHIND_TAGS]
        if not body_ids:
            continue
        body = coverage([layers[i] for i in body_ids]) > 0.5
        enclosed = ndimage.binary_fill_holes(lower) & ~lower
        gaps = ndimage.binary_opening((part.alpha > 0.5) & ~body, iterations=1)
        labels, count = ndimage.label(gaps)
        if not count:
            continue
        owner = owner_map(layers[:m])
        body_owner = owner_map([layers[i] for i in body_ids])
        for k, sl in enumerate(ndimage.find_objects(labels)):
            y0, y1 = max(0, sl[0].start - pad), sl[0].stop + pad
            x0, x1 = max(0, sl[1].start - pad), sl[1].stop + pad
            gap = labels[y0:y1, x0:x1] == k + 1
            if gap.sum() < min_px:
                continue
            ring = ndimage.binary_dilation(gap, iterations=2) & ~gap
            inside = enclosed[y0:y1, x0:x1][gap].all()
            rim_body = body_owner[y0:y1, x0:x1][ring]
            if not inside and (rim_body >= 0).mean() < body_share:
                continue
            if inside:
                rim = owner[y0:y1, x0:x1][ring]
                rim = rim[rim >= 0]
                if not len(rim):
                    continue
                layer = layers[int(np.bincount(rim).argmax())]
            else:
                gap &= _between(body[y0:y1, x0:x1], reach)
                if gap.sum() < min_px:
                    continue
                rim = rim_body[rim_body >= 0]
                layer = layers[body_ids[int(np.bincount(rim).argmax())]]
            view = layer.rgba[y0:y1, x0:x1]
            colors = _smooth_fill(view[..., :3].copy(), view[..., 3] > 0.5)
            view[..., :3] = np.where(gap[..., None], colors, view[..., :3])
            view[..., 3] = np.where(gap, 1.0, view[..., 3])
            filled += int(gap.sum())
    return filled


def _behind_arms(layers, margin, hidden=HIDDEN_TAGS):
    hidden_ids = [i for i, layer in enumerate(layers) if layer.tag in hidden]
    return hidden_ids, ndimage.binary_dilation(np.isin(owner_map(layers), hidden_ids), iterations=margin)


def _revealed(backdrop, guard):
    return ~ndimage.binary_dilation(backdrop["hidden"] > 0.02, iterations=guard)


def fill_hidden(layers, backdrops, guard=4, hidden=HIDDEN_TAGS):
    """Repaints what the arms and the held object cover with what the pose variants show there.

    See-Through guesses the art behind an arm; once the arm moves that guess shows. Each backdrop is a variant image
    split into layers and aligned to the base canvas. Every area behind an arm takes as much as it can from the one
    variant that reveals most of it. Only pixels the arms cover fully, on layers drawn below them, change, so the
    rest pose stays the same. `hidden` are the tags that cover what the variants reveal (ARM_TAGS with a static
    object).
    """
    hidden_ids = [i for i, layer in enumerate(layers) if layer.tag in hidden]
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


def backdrop_patch(layers, backdrop, name, side, margin=3, guard=4, hidden=HIDDEN_TAGS):
    """One variant's own art for the area its replaced arm used to cover, plus the tag that owns each pixel."""
    _, behind = _behind_arms(layers, margin, hidden)
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
