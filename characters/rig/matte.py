"""Which white pixels of the art are paper: an anime figure matte (ISNet, skytnt/anime-seg `isnetis.onnx`).

Generated art sits on white, and white also shows through an axe blade or between long hair and the body. Those
pockets are enclosed by line art, so filling the outline takes them in, and See-Through sometimes paints them into a
layer; either way they draw as white blobs on the dark desktop. A white gown looks the same pixel by pixel, so the
matte decides: paper is near-white art the matte calls background.
"""
from functools import lru_cache

import numpy as np
from PIL import Image
from scipy import ndimage

SIDE = 1024


@lru_cache(maxsize=1)
def _session(model):
    import onnxruntime as ort
    options = ort.SessionOptions()
    options.intra_op_num_threads = 4
    return ort.InferenceSession(model, options, providers=["CPUExecutionProvider"])


def figure(reference, model):
    """Soft figure matte (0 background, 1 character) of the square reference at its own size."""
    h, w = reference.shape[:2]
    small = np.asarray(Image.fromarray((reference * 255).astype(np.uint8)).resize((SIDE, SIDE), Image.LANCZOS),
                       np.float32) / 255
    session = _session(model)
    out = session.run(None, {session.get_inputs()[0].name: small.transpose(2, 0, 1)[None]})[0][0, 0]
    return np.asarray(Image.fromarray(out.astype(np.float32)).resize((w, h), Image.BILINEAR), np.float32)


def paper(reference, matte, white=0.96, cut=0.5, pocket=0.8, covered=None, unsure=0.99):
    """Near-white pixels the matte calls background, and whole enclosed near-white areas it leans towards calling
    background (a small hole in a blade can score halfway). With `covered` (where the split's layers are opaque), an
    enclosed area no layer covers counts as paper unless the matte is sure it is figure: a gap framed by raised arms
    can score as high as 0.93."""
    near = reference.min(axis=2) > white
    bare = near & (matte < cut)
    labels, count = ndimage.label(near)
    if count:
        index = range(1, count + 1)
        edge = np.unique(np.concatenate([labels[0], labels[-1], labels[:, 0], labels[:, -1]]))
        mean = np.asarray(ndimage.mean(matte, labels, index))
        loose = mean < pocket
        if covered is not None:
            loose |= (mean < unsure) & (np.asarray(ndimage.mean(covered, labels, index)) < 0.5)
        bare |= np.isin(labels, np.setdiff1d(1 + np.flatnonzero(loose), edge))
    return bare


def paper_in_boxes(reference, boxes, white=0.96):
    """Near-white pixels inside boxes given in canvas fractions (x0, y0, x1, y1): the `paper` hint, for a sliver of
    paper between a shoe and an axe's spike so narrow that the matte calls it figure."""
    h, w = reference.shape[:2]
    out = np.zeros((h, w), bool)
    near = reference.min(axis=2) > white
    for x0, y0, x1, y1 in boxes:
        ys, xs = slice(round(y0 * h), round(y1 * h)), slice(round(x0 * w), round(x1 * w))
        out[ys, xs] = near[ys, xs]
    return out


HAIR_TAGS = ("back hair", "front hair")


def hair_pockets(reference, parts, white=0.96, pure=0.985, min_px=20, hair_share=0.95, rim=0.75):
    """Paper between curls of long hair: near-white pockets below the chin, not on the image edge, of at least `min_px`,
    that only the hair layers cover (`hair_share` of the pocket, no other layer over half of it), that are as white
    as paper (mean darkest channel over `pure`; a highlight on the hair has the hair's tint) and that the hair's ink
    or color frames (mean darkest channel on a 2 px ring under `rim`: white hair around a highlight is light). The
    matte calls the whole mass of hair figure, so paper() keeps them."""
    face = next((p for p in parts if p.tag == "face"), None)
    if face is None:
        return np.zeros(reference.shape[:2], bool)
    chin = np.nonzero((face.alpha > 0.5).any(axis=1))[0].max()
    hair = np.zeros(reference.shape[:2], bool)
    other = np.zeros(reference.shape[:2], bool)
    for p in parts:
        if p.tag in HAIR_TAGS:
            hair |= p.alpha > 0.5
        else:
            other |= p.alpha > 0.5
    darkest = reference.min(axis=2)
    labels, count = ndimage.label(darkest > white)
    if not count:
        return np.zeros(reference.shape[:2], bool)
    index = range(1, count + 1)
    edge = np.unique(np.concatenate([labels[0], labels[-1], labels[:, 0], labels[:, -1]]))
    size = np.asarray(ndimage.sum(np.ones_like(darkest), labels, index))
    in_hair = np.asarray(ndimage.mean(hair, labels, index))
    in_other = np.asarray(ndimage.mean(other, labels, index))
    whiteness = np.asarray(ndimage.mean(darkest, labels, index))
    slices = ndimage.find_objects(labels)
    top = np.array([s[0].start if s is not None else 0 for s in slices])
    ok = (size >= min_px) & (in_hair >= hair_share) & (in_other < 0.5) & (whiteness > pure) & (top > chin)
    for k in np.flatnonzero(ok):
        box = tuple(slice(max(0, a.start - 3), a.stop + 3) for a in slices[k])
        pocket = labels[box] == k + 1
        ring = ndimage.binary_dilation(pocket, iterations=2) & ~pocket
        if not ring.any() or darkest[box][ring].mean() >= rim:
            ok[k] = False
    return np.isin(labels, np.setdiff1d(1 + np.flatnonzero(ok), edge))


def clear_paper(parts, bare):
    """Makes every layer transparent where the art shows bare paper. Returns how many layer pixels it cleared."""
    cleared = 0
    for layer in parts:
        hit = bare & (layer.alpha > 0)
        cleared += int(hit.sum())
        layer.rgba[..., 3] = np.where(hit, 0.0, layer.alpha)
    return cleared
