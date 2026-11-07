"""Builds a character package from a See-Through PSD and the original illustration.

  python3 -m characters.rig.build --psd claude.psd --reference claude.png --name claude --out characters/build/claude \
      [--variants variants.json]

The optional manifest lists pose and expression variants:
  {"variants": [{"id": "wave", "kind": "arm", "side": "right", "psd": "wave.psd", "image": "wave.png"},
                {"id": "smile", "kind": "face", "psd": "smile.psd", "image": "smile.png"}]}
"""
import argparse
import json

import numpy as np

from . import atlas, layers, mesh, package, params, pose, skeleton, variants


def landmark_summary(lm):
    out = {k: lm[k] for k in ("face", "faceWidth", "chin", "neck", "waist", "feet", "hem")}
    out["arms"] = {k: {"shoulder": a["shoulder"], "elbow": a["elbow"], "hand": a["hand"], "pose": a.get("tip") is not None}
                   for k, a in lm["arms"].items()}
    out["eyes"] = lm["eyes"]
    out["objectSide"] = lm.get("objectSide")
    for key in ("backHair", "lock"):
        if key in lm:
            out[key] = lm[key]
    return json.loads(json.dumps(out, default=float))


def ordered_parts(base, loaded):
    """Base parts keep their order; variant parts sit right above what they replace, raised arms on top."""
    index = {layer.name: i for i, layer in enumerate(base)}
    slots = [(float(i), layer, None) for i, layer in enumerate(base)]
    top = float(len(base))
    for v in loaded:
        for k, layer in enumerate(v.parts):
            if v.kind == "arm" and v.front and layer.tag != "backdrop":
                top += 0.01
                slots.append((top, layer, v))
                continue
            base_name = layer.name.split("@")[0]
            anchor = index.get(base_name, index.get(v.replaces[0], len(base)))
            slots.append((anchor + 0.5 + 0.001 * k, layer, v))
    return [(layer, v) for _, layer, v in sorted(slots, key=lambda s: s[0])]


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--psd", required=True)
    ap.add_argument("--reference", required=True)
    ap.add_argument("--name", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--variants")
    args = ap.parse_args(argv)

    size, parts = layers.load_psd(args.psd)
    reference = layers.load_reference(args.reference, size)
    layers.reproject(parts, reference)
    fx0, _, fx1, _ = skeleton.box(next(p for p in parts if p.tag == "face").alpha)
    parts = layers.split_pairs(parts, (fx0 + fx1) / 2)
    layers.grow_underlays(parts)
    rest_diff = float(np.abs(layers.composite(parts) - reference).mean() * 255)

    lm = skeleton.landmarks(parts)
    for side, joints in pose.arms(args.reference, size, (fx0 + fx1) / 2).items():
        if side in lm["arms"]:
            lm["arms"][side] = skeleton.arm_from_joints(joints)
    bones = skeleton.bones(lm)
    objects = next((p for p in parts if p.tag == "objects"), None)
    object_attach = skeleton.attach_object(objects, lm) if objects is not None else None
    object_side = object_attach.split(".", 1)[1] if object_attach else None
    lm["objectSide"] = object_side

    loaded = []
    if args.variants:
        base_names = [p.name for p in parts]
        for spec in variants.load_manifest(args.variants):
            v = variants.load(spec, reference, size, lm, object_side, base_names)
            loaded.append(v)
            if v.kind == "arm":
                bones.append(skeleton.Bone(f"arm.{v.side}@{v.id}", "chest", v.arm["shoulder"]))
                bones.append(skeleton.Bone(f"forearm.{v.side}@{v.id}", f"arm.{v.side}@{v.id}", v.arm["elbow"]))
    backdrops = [v.backdrop for v in loaded if v.backdrop is not None]
    patch_tags = {}
    if backdrops:
        for v in loaded:
            if v.backdrop is not None:
                patch, patch_tags[f"backdrop.{v.side}@{v.id}"] = layers.backdrop_patch(
                    parts, v.backdrop, f"backdrop.{v.side}@{v.id}", v.side)
                v.parts.insert(0, patch)
        filled, unknown = layers.fill_hidden(parts, backdrops)
        print(f"behind the arms: {filled} px repainted from variants, {unknown} px left as See-Through guessed")
        for v in loaded:
            v.backdrop = None
        rest_diff = float(np.abs(layers.composite(parts) - reference).mean() * 255)
    bone_index = {b.name: i for i, b in enumerate(bones)}

    meshes, report = [], []
    for order, (layer, v) in enumerate(ordered_parts(parts, loaded)):
        built = mesh.build_mesh(layer.alpha, layer.tag)
        if built is None:
            continue
        vertices, triangles = built
        if layer.tag == "backdrop":
            influences = skeleton.weights_by_owner(vertices, patch_tags[layer.name], lm)
        elif v is not None and v.kind == "arm":
            attach = f"forearm.{v.side}@{v.id}"
            influences = skeleton.weights_for(layer, vertices, lm, attach, arm=v.arm, suffix=f"@{v.id}")
        else:
            attach = object_attach if layer.tag == "objects" else None
            influences = skeleton.weights_for(layer, vertices, lm, attach)
        influences = [(b, w) for b, w in influences if b in bone_index]
        bone_idx, weights = skeleton.pack_weights(influences, bone_index)
        gaps = mesh.uncovered_pixels(layer.alpha, vertices, triangles)
        meshes.append((layer, order, vertices, triangles, bone_idx, weights))
        report.append(f"{layer.name:<22} v={len(vertices):5d} t={len(triangles):5d} uncovered={gaps}")

    boxes = [(layer.name, layer.rgba, atlas.part_box(vtx, size)) for layer, _, vtx, *_ in meshes]
    sheet, places = atlas.pack(boxes)
    entries = []
    for layer, order, vertices, triangles, bone_idx, weights in meshes:
        ax, ay, x0, y0 = places[layer.name]
        uv = vertices - np.array([x0, y0], np.float32) + np.array([ax, ay], np.float32)
        entries.append(package.part_entry(layer, order, vertices, uv, triangles, bone_idx, weights))

    plist, drivers, physics = params.build(lm, list(bone_index))
    vparams, vdrivers, part_drivers, vmeta = params.variant_controls(loaded, {e["name"] for e in entries})
    path = package.write(args.out, args.name, size, sheet, bones, plist + vparams, drivers + vdrivers, physics,
                         entries, landmark_summary(lm), part_drivers, vmeta)
    print("\n".join(report))
    for v in loaded:
        print(f"variant {v.id:<11} {v.kind:<4} side={v.side} front={v.front} parts={len(v.parts)} "
              f"registration={json.dumps(v.report)}")
    print(json.dumps({
        "package": str(path), "parts": len(entries), "bones": len(bones), "params": len(plist) + len(vparams),
        "variants": len(loaded), "vertices": int(sum(len(e["vertices"]) // 2 for e in entries)),
        "atlas": list(sheet.shape[1::-1]), "rest_mean_abs_diff": round(rest_diff, 3),
    }))


if __name__ == "__main__":
    main()
