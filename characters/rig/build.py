"""Builds a character package from a See-Through PSD and the original illustration.

  python3 -m characters.rig.build --psd claude.psd --reference claude.png --name claude --out characters/build/claude
"""
import argparse
import json

import numpy as np

from . import atlas, layers, mesh, package, params, skeleton


def landmark_summary(lm):
    out = {k: lm[k] for k in ("face", "faceWidth", "chin", "neck", "waist", "feet", "hem")}
    out["arms"] = {k: {"shoulder": a["shoulder"], "elbow": a["elbow"]} for k, a in lm["arms"].items()}
    out["eyes"] = lm["eyes"]
    for key in ("backHair", "lock"):
        if key in lm:
            out[key] = lm[key]
    return json.loads(json.dumps(out, default=float))


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--psd", required=True)
    ap.add_argument("--reference", required=True)
    ap.add_argument("--name", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args(argv)

    size, parts = layers.load_psd(args.psd)
    reference = layers.load_reference(args.reference, size)
    layers.reproject(parts, reference)
    fx0, _, fx1, _ = skeleton.box(next(p for p in parts if p.tag == "face").alpha)
    parts = layers.split_pairs(parts, (fx0 + fx1) / 2)
    layers.grow_underlays(parts)
    rest = layers.composite(parts)
    rest_diff = float(np.abs(rest - reference).mean() * 255)

    lm = skeleton.landmarks(parts)
    bones = skeleton.bones(lm)
    bone_index = {b.name: i for i, b in enumerate(bones)}

    meshes, report = [], []
    for order, layer in enumerate(parts):
        built = mesh.build_mesh(layer.alpha, layer.tag)
        if built is None:
            continue
        vertices, triangles = built
        attach = skeleton.attach_object(layer, lm) if layer.tag == "objects" else None
        influences = skeleton.weights_for(layer, vertices, lm, attach)
        influences = [(b, w) for b, w in influences if b in bone_index]
        bone_idx, weights = skeleton.pack_weights(influences, bone_index)
        gaps = mesh.uncovered_pixels(layer.alpha, vertices, triangles)
        meshes.append((layer, order, vertices, triangles, bone_idx, weights))
        report.append(f"{layer.name:<16} v={len(vertices):5d} t={len(triangles):5d} uncovered={gaps}")

    boxes = [(layer.name, layer.rgba, atlas.part_box(v, size)) for layer, _, v, *_ in meshes]
    sheet, places = atlas.pack(boxes)
    entries = []
    for layer, order, vertices, triangles, bone_idx, weights in meshes:
        ax, ay, x0, y0 = places[layer.name]
        uv = vertices - np.array([x0, y0], np.float32) + np.array([ax, ay], np.float32)
        entries.append(package.part_entry(layer, order, vertices, uv, triangles, bone_idx, weights))

    plist, drivers, physics = params.build(lm, list(bone_index))
    path = package.write(args.out, args.name, size, sheet, bones, plist, drivers, physics, entries, landmark_summary(lm))
    print("\n".join(report))
    print(json.dumps({
        "package": str(path), "parts": len(entries), "bones": len(bones), "params": len(plist),
        "vertices": int(sum(len(e["vertices"]) // 2 for e in entries)),
        "atlas": list(sheet.shape[1::-1]), "rest_mean_abs_diff": round(rest_diff, 3),
    }))


if __name__ == "__main__":
    main()
