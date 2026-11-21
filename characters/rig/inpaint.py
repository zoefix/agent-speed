"""What a moving part uncovers: each layer's paint where the layers above hide it, inpainted by LaMa.

See-Through guesses every layer's hidden art, and the build adds to it (layers grown under their neighbours, gaps
filled); once an arm, a lock of hair or a held object moves, that guess shows: dark slabs copied from an outline,
blotches, a ghost of the hand on a book. LaMa (big-lama, TorchScript `big-lama.pt` as lama-cleaner ships it) repaints
those pixels from what the layer shows around them, on the layer alone over its own mean color, so nothing of the
parts above leaks in. Only pixels the layers above cover fully change, so the rest pose stays the same.
"""
from functools import lru_cache

import numpy as np
from PIL import Image
from scipy import ndimage

from . import layers as L

# Pixels the layers above cover by more than COVER count as hidden.
COVER = 0.98
MIN_PX = 40
# Context around the hidden pixels, in px, and the largest side LaMa works at (bigger crops are scaled down).
MARGIN, MAX_SIDE = 32, 1024
# The mask reaches EDGE px past the hidden pixels, over the soft rim of the parts above, which carries their colors.
EDGE = 3
# Past the layer's outline the crop holds the layer's mean color, which LaMa would take for paint (a strip hidden along
# the outline, under a lock of hair, faded to grey): within OUTSIDE px of the hidden pixels it is masked as well, so the
# strip continues the paint beside it.
OUTSIDE = 24
THREADS = 4


@lru_cache(maxsize=1)
def _model(path):
    import torch
    torch.set_num_threads(THREADS)
    return torch.jit.load(path, map_location="cpu").eval()


def lama(image, mask, model_path):
    """Inpaints `mask` of `image` (H x W x 3 in 0..1). Returns the image with the masked pixels repainted."""
    import torch
    h, w = mask.shape
    k = min(1.0, MAX_SIDE / max(h, w))
    sw, sh = max(8, round(w * k)), max(8, round(h * k))
    pw, ph = -(-sw // 8) * 8, -(-sh // 8) * 8
    small = np.asarray(Image.fromarray((image * 255).astype(np.uint8)).resize((sw, sh), Image.LANCZOS), np.float32)
    small_mask = np.asarray(Image.fromarray(mask.astype(np.uint8) * 255).resize((sw, sh), Image.BILINEAR)) > 0
    small = np.pad(small / 255, ((0, ph - sh), (0, pw - sw), (0, 0)), mode="edge")
    small_mask = np.pad(small_mask, ((0, ph - sh), (0, pw - sw)))
    with torch.no_grad():
        out = _model(model_path)(torch.from_numpy(small.transpose(2, 0, 1).copy())[None],
                                 torch.from_numpy(small_mask.astype(np.float32))[None, None])
    out = np.clip(out[0].permute(1, 2, 0).numpy()[:sh, :sw], 0, 1)
    if k < 1:
        out = np.asarray(Image.fromarray((out * 255).astype(np.uint8)).resize((w, h), Image.LANCZOS), np.float32) / 255
    return np.where(mask[..., None], out, image)


def repaint_hidden(layers, model_path, skip=L.NO_GROWTH):
    """Repaints each layer where the layers above hide it (by more than COVER) with LaMa, from what it shows. Returns
    the pixels repainted. A lash tip lifted out of the hair does not hide it: the hair under it is already filled."""
    total = 0
    h, w = layers[0].rgba.shape[:2]
    for i, layer in enumerate(layers[:-1]):
        if layer.tag in skip:
            continue
        solid = layer.alpha > 0.5
        above = [other for other in layers[i + 1:] if not other.name.endswith(L.TIP)]
        if not above:
            continue
        hidden = solid & (L.coverage(above) > COVER)
        if hidden.sum() < MIN_PX or (solid & ~hidden).sum() < MIN_PX:
            continue
        ys, xs = np.nonzero(hidden)
        box = (slice(max(ys.min() - MARGIN, 0), min(ys.max() + MARGIN + 1, h)),
               slice(max(xs.min() - MARGIN, 0), min(xs.max() + MARGIN + 1, w)))
        rgb, alpha = layer.rgba[box][..., :3], layer.alpha[box][..., None]
        shown = (solid & ~hidden)[box]
        backdrop = rgb[shown].mean(axis=0) if shown.any() else rgb[solid[box]].mean(axis=0)
        image = rgb * alpha + backdrop * (1 - alpha)
        mask = ndimage.binary_dilation(hidden[box], iterations=EDGE) & solid[box]
        mask |= ndimage.binary_dilation(hidden[box], iterations=OUTSIDE) & ~solid[box]
        layer.rgba[box][..., :3] = np.where(hidden[box][..., None], lama(image, mask, model_path), rgb)
        total += int(hidden.sum())
    return total
