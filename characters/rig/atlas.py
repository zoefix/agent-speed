"""Packs part textures into one atlas, with colors bled into transparent texels so filtering never darkens edges."""
import numpy as np
from scipy import ndimage


def part_box(vertices, size, pad=3):
    w, h = size
    x0, y0 = np.floor(vertices.min(axis=0)).astype(int) - pad
    x1, y1 = np.ceil(vertices.max(axis=0)).astype(int) + pad
    return max(0, x0), max(0, y0), min(w, x1), min(h, y1)


def pack(items, width=2048, gap=4):
    """items: list of (name, rgba float canvas, box). Returns the atlas (uint8 RGBA) and {name: (ax, ay, x0, y0)}."""
    order = sorted(items, key=lambda it: it[2][3] - it[2][1], reverse=True)
    placements = {}
    x = y = shelf = 0
    for name, _, (x0, y0, x1, y1) in order:
        bw, bh = x1 - x0, y1 - y0
        if bw > width:
            raise ValueError(f"part {name} is wider than the atlas")
        if x + bw > width:
            x, y, shelf = 0, y + shelf + gap, 0
        placements[name] = (x, y, x0, y0)
        x += bw + gap
        shelf = max(shelf, bh)
    height = y + shelf
    atlas = np.zeros((height, width, 4), np.float32)
    for name, rgba, (x0, y0, x1, y1) in items:
        ax, ay, _, _ = placements[name]
        atlas[ay:ay + y1 - y0, ax:ax + x1 - x0] = rgba[y0:y1, x0:x1]
    return bleed(atlas), placements


def bleed(atlas):
    solid = atlas[..., 3] > 0.01
    if solid.any():
        _, (iy, ix) = ndimage.distance_transform_edt(~solid, return_indices=True)
        atlas[..., :3] = np.where(solid[..., None], atlas[..., :3], atlas[iy, ix, :3])
    return (np.clip(atlas, 0, 1) * 255).round().astype(np.uint8)
