"""Builds a character package from a See-Through PSD and the original illustration.

  python3 -m characters.rig.build --psd claude.psd --reference claude.png --name claude --out characters/build/claude \
      [--variants variants.json]

The optional manifest lists pose and expression variants:
  {"variants": [{"id": "wave", "kind": "arm", "side": "right", "psd": "wave.psd", "image": "wave.png"},
                {"id": "smile", "kind": "face", "psd": "smile.psd", "image": "smile.png"}]}
"""
import argparse
import json
from pathlib import Path

import numpy as np

from . import appendages, atlas, hands, layers, mesh, package, params, pose, skeleton, variants


def landmark_summary(lm):
    out = {k: lm[k] for k in ("face", "faceWidth", "chin", "neck", "waist", "feet", "hem", "skirtMid")}
    if "foot" in lm:
        out["foot"] = lm["foot"]
    out["arms"] = {k: {"shoulder": a["shoulder"], "elbow": a["elbow"], "hand": a["hand"], "pose": a.get("tip") is not None}
                   for k, a in lm["arms"].items()}
    out["eyes"] = lm["eyes"]
    out["objectSide"] = lm.get("objectSide")
    for key in ("backHair", "lock", "tail", "wings", "animalEars"):
        if key in lm:
            out[key] = lm[key]
    out["hands"] = {side: hands.summary(h) for side, h in lm.get("hands", {}).items()}
    return json.loads(json.dumps(out, default=float))


def add_appendages(parts, lm, bones):
    """Finds the tail, the wings and animal ears, adds their bones and their landmarks to `lm`."""
    tail = next((p for p in parts if p.tag == "tail"), None)
    tail_lm = appendages.tail_landmarks(tail, parts, lm["waist"]) if tail is not None else None
    if tail_lm is not None:
        parent = "lower"
        for name, pivot in zip(appendages.TAIL_BONES, tail_lm["pivots"]):
            bones.append(skeleton.Bone(name, parent, pivot))
            parent = name
        lm["tail"] = {k: tail_lm[k] for k in ("root", "pivots", "ends", "length")}
    wings = [p for p in parts if p.tag == "wings"]
    spine = (lm["neck"][0], lm["chin"] + 0.45 * (lm["waist"][1] - lm["chin"]))
    wing_lm = appendages.wing_landmarks(wings, spine) if wings else {}
    for side, w in wing_lm.items():
        bones.append(skeleton.Bone(f"wing.{side}", "chest", w["root"]))
    if wing_lm:
        lm["wings"] = wing_lm
    ear_lm = appendages.animal_ears(parts, lm["face"])
    for side, e in ear_lm.items():
        bones.append(skeleton.Bone(f"ear.{side}", "side", e["base"]))
    if ear_lm:
        lm["animalEars"] = {side: {k: e[k] for k in ("base", "tip")} for side, e in ear_lm.items()}
    return tail_lm, wing_lm, ear_lm, spine


def split_hands(parts, found, reference, sam):
    """Cuts each detected hand out of its arm layer into its own part, drawn right above the arm. With a SAM
    checkpoint the cut follows the drawn outline of the hand."""
    masks = {}
    if sam:
        from . import segment
        for side, hand in found.items():
            mask = segment.hand_mask(reference, hand, sam)
            if mask is not None:
                masks[side] = mask
                cleared = hands.clear_ghosts(parts, mask & hands.past_cut(mask.shape, hand), keep=f"handwear.{side}")
                print(f"hand {side}: {int(mask.sum())} px from SAM, {cleared} px of other layers inpainted")
            else:
                print(f"hand {side}: SAM found no clean mask, the hand stays part of its arm")
        found = {side: hand for side, hand in found.items() if side in masks}
    out = []
    for layer in parts:
        hand = found.get(layer.side) if layer.tag == "handwear" else None
        if hand is None:
            out.append(layer)
            continue
        out += list(hands.split(layer, hand, masks.get(layer.side), reference))
    return out, found


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
    ap.add_argument("--sam", help="Segment Anything ViT-B checkpoint for precise hand outlines")
    ap.add_argument("--spec", help="the character's spec: its `rig` hints (recover, tail, wings, ears, staticObjects)")
    ap.add_argument("--matte", help="anime figure matte (isnetis.onnx) that tells bare paper from white art")
    args = ap.parse_args(argv)
    hints = json.loads(Path(args.spec).read_text()).get("rig", {}) if args.spec else {}

    size, parts = layers.load_psd(args.psd)
    reference = layers.load_reference(args.reference, size)
    bare = None
    if args.matte:
        from . import matte
        bare = matte.paper(reference, matte.figure(reference, args.matte))
        print(f"bare paper: {int(bare.sum())} px, {matte.clear_paper(parts, bare)} layer px cleared")
    recovered = appendages.recover_missing(parts, reference, hints, bare=bare) if hints.get("recover") else []
    if recovered:
        print(f"recovered from the art no layer covered: {', '.join(recovered)}")
    layers.reproject(parts, reference)
    fx0, _, fx1, _ = skeleton.box(next(p for p in parts if p.tag == "face").alpha)
    parts = layers.split_pairs(parts, (fx0 + fx1) / 2)
    layers.grow_underlays(parts)
    rest_diff = float(np.abs(layers.composite(parts) - reference).mean() * 255)

    lm = skeleton.landmarks(parts)
    for side, joints in pose.arms(args.reference, size, (fx0 + fx1) / 2).items():
        if side in lm["arms"]:
            lm["arms"][side] = skeleton.arm_from_joints(joints)
    lm["hands"] = {}
    for side, points in pose.hands(args.reference, size, (fx0 + fx1) / 2).items():
        if side in lm["arms"] and lm["arms"][side].get("tip") is not None:
            lm["hands"][side] = dict(hands.landmarks(points, lm["arms"][side]["elbow"]), side=side)
    parts, lm["hands"] = split_hands(parts, lm["hands"], reference, args.sam)
    bones = skeleton.bones(lm)
    for hand in lm["hands"].values():
        bones += hands.bones(hand, skeleton.Bone)
    tail_lm, wing_lm, ear_lm, spine = add_appendages(parts, lm, bones)
    objects = next((p for p in parts if p.tag == "objects"), None)
    object_attach = skeleton.attach_object(objects, lm) if objects is not None else None
    if object_attach and object_attach.split(".", 1)[1] in lm["hands"]:
        object_attach = "hand." + object_attach.split(".", 1)[1]
    object_side = object_attach.split(".", 1)[1] if object_attach else None
    if hints.get("staticObjects"):
        object_attach = "lower"
    lm["objectSide"] = object_side

    loaded = []
    if args.variants:
        base_names = [p.name for p in parts]
        for spec in variants.load_manifest(args.variants):
            v = variants.load(spec, reference, size, lm, object_side, base_names)
            loaded.append(v)
            if v.kind == "arm":
                bones.append(skeleton.Bone(f"arm.{v.side}@{v.id}", f"shoulder.{v.side}", v.arm["shoulder"]))
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
        if layer.tag == "hand":
            influences = hands.weights(vertices, lm["hands"][layer.side])
        elif layer.tag == "tail" and tail_lm is not None:
            influences = appendages.tail_weights(vertices, tail_lm)
        elif layer.tag == "wings" and wing_lm:
            influences = appendages.wing_weights(vertices, wing_lm, spine)
        elif layer.tag == "ears" and layer.side in ear_lm and ear_lm[layer.side]["layer"] == layer.name:
            influences = [(f"ear.{layer.side}", np.ones(len(vertices)))]
        elif layer.tag == "backdrop":
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
