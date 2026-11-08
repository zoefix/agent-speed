"""Which white pixels of the art are paper: an anime figure matte (ISNet, skytnt/anime-seg `isnetis.onnx`).

Generated art sits on white, and white also shows through an axe blade or between long hair and the body. Those
pockets are enclosed by line art, so filling the outline takes them in, and See-Through sometimes paints them into a
layer; either way they draw as white blobs on the dark desktop. A white gown looks the same pixel by pixel, so the
matte decides: paper is near-white art the matte calls background.
"""
from functools import lru_cache

import numpy as np
from PIL import Image

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


def paper(reference, matte, white=0.96, cut=0.5):
    """Near-white pixels the matte calls background."""
    return (reference.min(axis=2) > white) & (matte < cut)


def clear_paper(parts, bare):
    """Makes every layer transparent where the art shows bare paper. Returns how many layer pixels it cleared."""
    cleared = 0
    for layer in parts:
        hit = bare & (layer.alpha > 0)
        cleared += int(hit.sum())
        layer.rgba[..., 3] = np.where(hit, 0.0, layer.alpha)
    return cleared
