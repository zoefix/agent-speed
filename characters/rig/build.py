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

from ..reference.motions.profile import AT_MOUTH
from . import appendages, atlas, hands, layers, mesh, package, params, pose, skeleton, variants


def landmark_summary(lm):
    out = {k: lm[k] for k in ("face", "faceWidth", "chin", "neck", "waist", "feet", "hem", "skirtMid")}
    if "foot" in lm:
        out["foot"] = lm["foot"]
    out["arms"] = {k: {"shoulder": a["shoulder"], "elbow": a["elbow"], "hand": a["hand"], "pose": a.get("tip") is not None}
                   for k, a in lm["arms"].items()}
    out["eyes"] = lm["eyes"]
    out["objectSide"] = lm.get("objectSide")
    for key in ("torsoLength", "backHair", "lock", "tail", "wings", "animalEars"):
        if key in lm:
            out[key] = lm[key]
    out["hands"] = {side: hands.summary(h) for side, h in lm.get("hands", {}).items()}
    return json.loads(json.dumps(out, default=float))


def add_appendages(parts, lm, bones, hints):
    """Finds the tail, the wings and (with the `ears` hint) animal ears, adds their bones and their landmarks to
    `lm`."""
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
    ear_lm = appendages.animal_ears(parts, lm["face"]) if hints.get("ears") else {}
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


PROP_FOLLOW = ("chest", "lower", "head")


def prop_hint(hints, band):
    """The band's prop from the `props` hint: points, or {"points", "follow"} for a prop that moves with the body
    (a book held up before the chest, which the split also paints into the top) instead of the nearest hand."""
    prop = hints.get("props", {}).get(band) if band else None
    if isinstance(prop, dict):
        if prop.get("follow") not in (None,) + PROP_FOLLOW:
            raise ValueError(f"props.{band}.follow must be one of {PROP_FOLLOW}")
        return prop["points"], prop.get("follow")
    return prop, None


def object_bone(layer, lm):
    """The bone an object follows: the hand holding it (within 0.6 face widths of it; for a prop, the hand nearest
    it), or its forearm when that hand has no rig; an object no hand holds goes with the body, the hips below the
    waist and the chest above."""
    if layer.name.startswith("prop"):
        bone = skeleton.attach_object(layer, lm)
        side = bone.split(".", 1)[1] if bone else None
    else:
        side = skeleton.holding_side(layer, lm, 0.6 * lm["faceWidth"])
    if side is None:
        ys = np.nonzero(layer.alpha > 0.3)[0]
        return "lower" if len(ys) and ys.mean() > lm["waist"][1] else "chest"
    return f"hand.{side}" if side in lm["hands"] else f"forearm.{side}"


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
    ap.add_argument("--spec", help="the character's spec: its `rig` hints "
                                   "(recover, tail, wings, ears, staticObjects, legsInSkirt, rigidLower, bodyInArms, props)")
    ap.add_argument("--matte", help="anime figure matte (isnetis.onnx) that tells bare paper from white art")
    ap.add_argument("--band", help="the pose band this art shows, for band-specific hints (props)")
    args = ap.parse_args(argv)
    character = json.loads(Path(args.spec).read_text()) if args.spec else {}
    hints = character.get("rig", {})

    size, parts = layers.load_psd(args.psd)
    reference = layers.load_reference(args.reference, size)
    points, follow = prop_hint(hints, args.band)
    if points and args.sam:
        from . import segment
        prop = segment.prop_mask(reference, [(x * size[0], y * size[1]) for x, y in points], args.sam)
        parts = layers.take_prop(parts, prop, reference)
        print(f"prop: {int(prop.sum())} px from SAM moved into one object layer")
    bare = figure = None
    if args.matte:
        from . import matte
        figure = matte.figure(reference, args.matte)
        bare = matte.paper(reference, figure, covered=layers.coverage(parts) > 0.5)
        print(f"bare paper: {int(bare.sum())} px, {matte.clear_paper(parts, bare)} layer px cleared")
    recovered = (appendages.recover_missing(parts, reference, hints, bare=bare, figure=figure)
                 if hints.get("recover") else [])
    if recovered:
        print(f"recovered from the art no layer covered: {', '.join(recovered)}")
    if hints.get("recover"):
        print(f"fragments attached to the layer they were cut from: "
              f"{appendages.attach_fragments(parts, reference, bare=bare, figure=figure)}")
        print(f"holes between layers filled: {appendages.fill_holes(parts, reference, bare=bare)} px")
    moved = layers.tail_from_hair(parts, reference, skeleton.box(next(p for p in parts if p.tag == "face").alpha))
    if moved:
        print(f"tail drawn into the front hair given back to the tail: {moved} px")
    tail_layer = next((p for p in parts if p.tag == "tail"), None)
    if tail_layer is not None and args.sam:
        from . import segment
        moved = layers.tail_from_back_hair(parts, segment.tail_mask(reference, tail_layer.alpha, args.sam))
        if moved:
            print(f"tail drawn into the back hair given back to the tail: {moved} px")
    layers.seal_tail(parts, reference)
    parts, copies = layers.tail_alone(parts, reference)
    if copies:
        print(f"copies of the tail repainted in: {', '.join(copies)}")
    layers.reproject(parts, reference)
    fx0, _, fx1, _ = skeleton.box(next(p for p in parts if p.tag == "face").alpha)
    parts = layers.split_pairs(parts, (fx0 + fx1) / 2)
    arm_joints = pose.arms(args.reference, size, (fx0 + fx1) / 2)
    parts = layers.split_objects(layers.drop_empty(layers.split_by_arms(parts, arm_joints, reference)))
    face_box = skeleton.box(next(p for p in parts if p.tag == "face").alpha)
    if hints.get("bodyInArms"):
        parts, moved = layers.split_body_from_arms(parts, arm_joints, reference, face_box)
        print(f"body drawn into the arms cut out along the ink lines: {moved} px")
    parts, hats = layers.lift_hats(parts, face_box)
    if hats:
        print(f"hats drawn into the top given to the head: {hats}")
    print(f"hidden legs repainted from the legs that show: {layers.fill_hidden_legs(parts)} px")
    layers.grow_underlays(parts)
    rest_diff = float(np.abs(layers.composite(parts) - reference).mean() * 255)

    torso = pose.torso(args.reference, size)
    lm = skeleton.landmarks(parts, torso)
    if torso:
        lm["torsoLength"] = torso["length"]
    if character.get("motion", {}).get("seated") or hints.get("rigidLower"):
        lm.pop("foot", None)
        lm["rigidLower"] = True
    elif hints.get("legsInSkirt") and any(p.tag == "bottomwear" for p in parts):
        lm.pop("foot", None)
        lm["legsInSkirt"] = True
    for side, joints in arm_joints.items():
        if side in lm["arms"]:
            lm["arms"][side] = skeleton.arm_from_joints(joints)
    lm["bodyInArms"] = bool(hints.get("bodyInArms"))
    lm["hands"] = {}
    for side, points in pose.hands(args.reference, size, (fx0 + fx1) / 2).items():
        if side in lm["arms"] and lm["arms"][side].get("tip") is not None:
            lm["hands"][side] = dict(hands.landmarks(points, lm["arms"][side]["elbow"]), side=side)
    parts, lm["hands"] = split_hands(parts, lm["hands"], reference, args.sam)
    bones = skeleton.bones(lm)
    for hand in lm["hands"].values():
        bones += hands.bones(hand, skeleton.Bone)
    tail_lm, wing_lm, ear_lm, spine = add_appendages(parts, lm, bones, hints)
    if tail_lm is not None:
        backed = layers.back_tail_root(parts, tail_lm["root"], 0.35 * tail_lm["length"])
        print(f"behind the tail's root filled from the layers under it: {backed} px")
    held = sorted(((float(p.alpha.sum()), object_bone(p, lm)) for p in parts if p.tag == "objects"), reverse=True)
    object_side = next((bone.split(".", 1)[1] for _, bone in held if bone.split(".")[0] in ("hand", "forearm")), None)
    object_attach = {p.name: object_bone(p, lm) for p in parts if p.tag == "objects"}
    for name in object_attach:
        if hints.get("staticObjects") and not name.startswith("prop"):
            object_attach[name] = "lower"
    if follow and "prop" in object_attach:
        object_attach["prop"] = follow
    if character.get("motion", {}).get("poseHands", {}).get(args.band) in AT_MOUTH:
        # An instrument at the lips goes with the head; the director holds head and arms still while it plays.
        for name, bone in object_attach.items():
            if bone.split(".")[0] in ("hand", "forearm"):
                object_attach[name] = "head"
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
            attach = object_attach.get(layer.name.split("@")[0]) if layer.tag == "objects" else None
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
