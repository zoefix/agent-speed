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


def clear_paper(parts, bare):
    """Makes every layer transparent where the art shows bare paper. Returns how many layer pixels it cleared."""
    cleared = 0
    for layer in parts:
        hit = bare & (layer.alpha > 0)
        cleared += int(hit.sum())
        layer.rgba[..., 3] = np.where(hit, 0.0, layer.alpha)
    return cleared
