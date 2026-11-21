"""Builds a character package from a See-Through PSD and the original illustration.

  python3 -m characters.rig.build --psd claude.psd --reference claude.png --name claude --out characters/build/claude \
      [--variants variants.json]

The optional manifest lists pose and expression variants:
  {"variants": [{"id": "wave", "kind": "arm", "side": "right", "psd": "wave.psd", "image": "wave.png"},
                {"id": "smile", "kind": "face", "psd": "smile.psd", "image": "smile.png"},
                {"id": "expr-laugh", "kind": "patch", "region": "face", "image": "expr-laugh.png"}]}

A patch is an edit of the pose image that changes only the face (region `face`, `mouth` or `eyes`); it needs no PSD.
An edit whose registration fails or that barely changes its region is left out with a message, and so is an arm
variant whose split has no layer for that arm.

The build refuses a pose (exit status 1, no package left at --out) whose rig at rest does not match the art, or that
draws no feet while the character's idle pose (--idle, its built package) does (checks.py). --check stops after
the layers and prints those checks without building a package.
"""
import argparse
import json
from pathlib import Path

import numpy as np
from scipy import ndimage

from ..reference.motions.profile import AT_MOUTH
from . import appendages, atlas, checks, hands, layers, mesh, package, params, patches, pose, skeleton, variants


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
    checkpoint the cut follows the drawn outline of the hand, taken from the art, so a hand the split gave to another
    layer (the other sleeve, holding a book across the body) is whole all the same and that layer gives it up."""
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
        mask = masks.get(layer.side)
        holds = mask is None or any((ndimage.binary_dilation(p.alpha > 0.3, iterations=6) & mask).any()
                                    for p in parts if p.tag == "objects")
        out += list(hands.split(layer, hand, mask, reference, holds=holds))
    for side, mask in masks.items():
        part = next((p for p in out if p.tag == "hand" and p.side == side), None)
        moved = hands.bits_in_hair(out, part, mask) if part is not None else 0
        if moved:
            print(f"hand {side}: {moved} px of it the front hair drew moved to the hand")
    # An arm whose hand has no part (the pose model missed it) takes back fingertips a headphone or a hat drew beside
    # it too, judged by the face's skin (not from the hair: skin between strands there is the shoulder's).
    face = next((p for p in out if p.tag == "face"), None)
    if face is not None and (face.alpha > 0.9).sum() >= 50:
        tone = np.median(hands._chroma(face.rgba[..., :3])[face.alpha > 0.9], axis=0)
        for arm in [p for p in out if p.tag == "handwear" and p.side in ("left", "right")]:
            if any(p.tag == "hand" and p.side == arm.side for p in out):
                continue
            moved = hands.bits_in_hair(out, arm, arm.alpha > 0.5, tone=tone, over=("earwear", "headwear", "ears"))
            if moved:
                print(f"arm {arm.side}: {moved} px of its hand drawn over the head moved to the arm")
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


def touches_hand(layer, parts, side, reach=6):
    """Whether the hand of `side` (its hand layer, or its sleeve when the hand has none) comes within `reach` px of an
    object piece."""
    near = ndimage.binary_dilation(layer.alpha > 0.3, iterations=reach)
    own = [p for p in parts if p.tag == "hand" and p.side == side] or \
          [p for p in parts if p.tag == "handwear" and p.side == side]
    return any((near & (p.alpha > 0.5)).any() for p in own)


def hands_clasped(parts, share=0.15):
    """Whether one hand lies over the other (clasped, or one resting on the other) and hides at least `share` of it:
    what it hides is a guess, a grey blob once either hand moves away."""
    hands = [p for p in parts if p.tag == "hand" and p.side in ("left", "right")]
    if len({p.side for p in hands}) < 2:
        return False
    lower, upper = hands[0], hands[-1]
    solid = lower.alpha > 0.5
    return bool(solid.any()) and float((solid & (upper.alpha > 0.5)).sum() / solid.sum()) >= share


HEAD_HELD = layers.FACE_TAGS + ("ears", "earwear")


def hands_at_face(parts, min_px=30):
    """Sides whose hand lies on the face or an ear by at least `min_px` (a finger at the chin, a fist rubbing an eye,
    a hand on a headphone): what it hides there is a guess, a smear once the hand moves off, or a piece of the ear
    carried away."""
    face = layers.coverage([p for p in parts if p.tag in HEAD_HELD]) > 0.5
    return sorted({p.side for p in parts if p.tag == "hand" and p.side in ("left", "right")
                   and int(((p.alpha > 0.5) & face).sum()) >= min_px})


def hands_on_body(parts, share=0.7):
    """Sides whose hand, or whose whole arm, lies over the other layers (the body, the clothes, an object, the hair)
    by `share` of it: what it hides there is a guess, a smear or the old hand's outline once the hand sways off, a
    slab beside the coat once a long sleeve lying on it swings."""
    arms = ("hand", "handwear")
    under = layers.coverage([p for p in parts if p.tag not in arms]) > 0.5
    out = []
    for p in parts:
        if p.tag in arms and p.side in ("left", "right"):
            drawn = p.alpha > 0.5
            if drawn.any() and float((drawn & under).sum() / drawn.sum()) >= share:
                out.append(p.side)
    return sorted(set(out))


def lock_on_body(parts, lm):
    """Share of the front hair's lock (what hangs below the chin, as skeleton.landmarks finds it) lying over the body's
    layers rather than over the stage or the back hair."""
    hair = next((p for p in parts if p.name == "front hair"), None)
    if hair is None:
        return 0.0
    _, fy0, _, fy1 = lm["face"]
    lock = hair.alpha > 0.3
    lock[:int(lm["chin"] + 0.15 * (fy1 - fy0))] = False
    body = layers.coverage([p for p in parts[:parts.index(hair)] if p.tag != "back hair"]) > 0.5
    return float((lock & body).sum() / lock.sum()) if lock.any() else 0.0


def touches_arm(layer, parts, side, reach=6):
    """Whether the arm of `side` (its sleeve or hand layers) comes within `reach` px of an object piece."""
    near = ndimage.binary_dilation(layer.alpha > 0.3, iterations=reach)
    return any((near & (p.alpha > 0.5)).any() for p in parts if p.tag in ("hand", "handwear") and p.side == side)


def object_bone(layer, lm, parts=None):
    """The bone an object follows: the hand holding it (within 0.6 face widths of it and, given the `parts`, touched
    by that arm's sleeve or hand; for a prop, the hand nearest it), or its forearm when that hand has no rig; an object
    no hand holds (a pendant hanging from the belt by the hand) goes with the body, the hips below the waist and the
    chest above."""
    if layer.name.startswith("prop"):
        bone = skeleton.attach_object(layer, lm)
        side = bone.split(".", 1)[1] if bone else None
    else:
        side = skeleton.holding_side(layer, lm, 0.6 * lm["faceWidth"])
        if side is not None and parts is not None and not touches_arm(layer, parts, side):
            side = None
    if side is None:
        ys = np.nonzero(layer.alpha > 0.3)[0]
        return "lower" if len(ys) and ys.mean() > lm["waist"][1] else "chest"
    return f"hand.{side}" if side in lm["hands"] else f"forearm.{side}"


PATCH_ORDER = ("face", "mouth", "eyes")


def ordered_parts(base, loaded):
    """Base parts keep their order; variant parts sit right above what they replace, raised arms on top. Face patches
    lie above the face by region, eyes over mouths over whole faces, so a blink or a talking mouth shows over an
    expression."""
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
            region = 0.01 * PATCH_ORDER.index(v.region) if v.kind == "patch" else 0.0
            slots.append((anchor + 0.5 + region + 0.001 * k, layer, v))
    return [(layer, v) for _, layer, v in sorted(slots, key=lambda s: s[0])]


def refuse(name, out, problems):
    """Fails the build of `name` with its `problems`, leaving no package at `out`: a package built before would be
    packed in its place."""
    if out:
        for file in ("rig.json", "atlas.png"):
            (Path(out) / file).unlink(missing_ok=True)
    raise SystemExit(f"{name}: refused, no package written\n" + "\n".join(f"  {p}" for p in problems))


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--psd", required=True)
    ap.add_argument("--reference", required=True)
    ap.add_argument("--name", required=True)
    ap.add_argument("--out")
    ap.add_argument("--variants")
    ap.add_argument("--sam", help="Segment Anything ViT-B checkpoint for precise hand outlines")
    ap.add_argument("--spec", help="the character's spec: its `rig` hints (tail, tailSwing, wings, ears, "
                                   "staticObjects, legsInSkirt, rigidLower, bodyInArms, props)")
    ap.add_argument("--matte", help="anime figure matte (isnetis.onnx) that tells bare paper from white art")
    ap.add_argument("--lama", help="LaMa inpainting model (big-lama.pt, TorchScript) that repaints what moving parts "
                                   "uncover")
    ap.add_argument("--band", help="the pose band this art shows, for band-specific hints (props)")
    ap.add_argument("--idle", help="the character's idle pose (its built package): refuse this pose if it draws no "
                                   "feet while the idle pose does")
    ap.add_argument("--check", action="store_true", help="stop after the layers and print the checks as JSON "
                                                         "(exit status 1 when one fails); no package")
    args = ap.parse_args(argv)
    if not args.out and not args.check:
        ap.error("--out is required unless --check")
    character = json.loads(Path(args.spec).read_text()) if args.spec else {}
    hints = character.get("rig", {})

    size, parts = layers.load_psd(args.psd)
    if not hints.get("tail"):
        # A character without the `tail` hint has no tail: See-Through's guess of one (a plush toy in her arms) is
        # an object, which follows the hands that hold it instead of swishing.
        for p in parts:
            if p.tag == "tail":
                p.tag = "objects"
                print(f"{p.name}: no tail hint, rigged as an object")
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
        pockets = matte.hair_pockets(reference, parts) & ~bare
        if pockets.any():
            print(f"paper between curls of hair: {int(pockets.sum())} px")
            bare |= pockets
        print(f"bare paper: {int(bare.sum())} px, {matte.clear_paper(parts, bare)} layer px cleared")
    print("\n".join(appendages.recover(parts, reference, hints, bare=bare, figure=figure)))
    face_box = skeleton.box(next(p for p in parts if p.tag == "face").alpha)
    moved = layers.features_off_face(parts, face_box)
    if moved:
        print(f"eyes or mouths drawn away from the face given to the layer under them: {moved} px")
    parts, moved = layers.tail_off_head(parts, face_box)
    if moved:
        print(f"tail on the hat given to the hat: {moved} px")
    moved = layers.tail_from_hair(parts, reference, face_box)
    if moved:
        print(f"tail drawn into the front hair given back to the tail: {moved} px")
    tail_layer = next((p for p in parts if p.tag == "tail"), None)
    if tail_layer is not None and args.sam:
        from . import segment
        moved = layers.tail_from_back_hair(parts, segment.tail_mask(reference, tail_layer.alpha, args.sam))
        if moved:
            print(f"tail drawn into the back hair given back to the tail: {moved} px")
    layers.seal_tail(parts, reference, paper=bare)
    parts, copies = layers.tail_alone(parts, reference)
    wing_copies = layers.wings_alone(parts)
    if wing_copies:
        print(f"copies of the wings cleared from the layers under them: {wing_copies} px")
    if copies:
        print(f"copies of the tail repainted in: {', '.join(copies)}")
    arms_taken = layers.objects_over_arms(parts, reference)
    parts, over_face = layers.arms_over_face(parts, reference)
    parts, ears_moved = layers.earrings_over_hair(parts, reference)
    if ears_moved:
        print(f"earrings the art shows over the locks drawn above them: {ears_moved} px")
    on_face = layers.objects_over_face(parts)
    if on_face.any():
        print(f"objects the art shows over the face, kept by face patches: {int(on_face.sum())} px")
    if over_face:
        print(f"arms the art shows over the face drawn above it: {over_face} px")
    owner = layers.reproject(parts, reference)
    print(f"hidden paint settled: {layers.clean_hidden(parts, owner)} px")
    print(f"rims of moving parts taken off the layers under them: {layers.unmix_rims(parts, reference, owner)} px")
    fx0, _, fx1, _ = skeleton.box(next(p for p in parts if p.tag == "face").alpha)
    parts = layers.split_pairs(parts, (fx0 + fx1) / 2)
    print(f"lashes the bangs drew taken out of the hair: {layers.lashes_off_hair(parts)} px")
    print(f"back hair far behind the body cleared: {layers.back_hair_alone(parts)} px")
    arm_joints = pose.arms(args.reference, size, (fx0 + fx1) / 2)
    parts = layers.split_objects(layers.drop_empty(layers.split_by_arms(parts, arm_joints, reference)))
    parts, behind = layers.arms_behind_objects(parts, arms_taken)
    if behind:
        print(f"arms behind a held object the art shows over them: {behind} px")
    face_box = skeleton.box(next(p for p in parts if p.tag == "face").alpha)
    if hints.get("bodyInArms"):
        parts, moved = layers.split_body_from_arms(parts, arm_joints, reference, face_box)
        print(f"body drawn into the arms cut out along the ink lines: {moved} px")
    parts, hats, hat_moved = layers.lift_hats(parts, face_box)
    if hats:
        print(f"hats drawn into the top given to the head: {hats}")
    print(f"hidden legs repainted from the legs that show: {layers.fill_hidden_legs(parts)} px")
    layers.grow_underlays(parts)
    print(f"gaps under moving parts filled: {layers.fill_enclosed_under(parts)} px")
    shadow = appendages.floor_shadow(parts, reference, figure, bare=bare)
    rest_ignore = hat_moved if shadow is None else hat_moved | shadow
    feet = checks.has_feet(checks.layer_bottoms(parts), size[1])
    problems = checks.feet_problems(checks.pose_has_feet(args.idle), feet) if args.idle else []
    if args.check:
        rest = checks.rest_metrics(layers.composite(parts), reference, rest_ignore)
        problems += checks.rest_problems(rest)
        print(json.dumps({"check": args.name, "feet": feet, "footwear": any(p.tag == checks.FEET for p in parts),
                          **rest, "problems": problems}))
        raise SystemExit(1 if problems else 0)
    if problems:
        refuse(args.name, args.out, problems)

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
    held = sorted(((float(p.alpha.sum()), object_bone(p, lm, parts)) for p in parts if p.tag == "objects"),
                  reverse=True)
    object_side = next((bone.split(".", 1)[1] for _, bone in held if bone.split(".")[0] in ("hand", "forearm")), None)
    object_attach = {p.name: object_bone(p, lm, parts) for p in parts if p.tag == "objects"}
    for name in object_attach:
        if hints.get("staticObjects") and not name.startswith("prop"):
            object_attach[name] = "lower"
    if follow and "prop" in object_attach:
        object_attach["prop"] = follow
    playing = character.get("motion", {}).get("poseHands", {}).get(args.band) in AT_MOUTH
    if playing:
        # An instrument at the lips goes with the head; the director holds head and arms still while it plays.
        for name, bone in object_attach.items():
            if bone.split(".")[0] in ("hand", "forearm"):
                object_attach[name] = "head"
    lm["objectSide"] = object_side
    # An object both hands hold (an abacus) stays with the body; both arms then keep near it (params.TWO_HANDED_SWING).
    for p in parts:
        if (p.tag == "objects" and not p.name.startswith("prop") and not hints.get("staticObjects")
                and all(touches_hand(p, parts, s) for s in ("left", "right"))):
            ys = np.nonzero(p.alpha > 0.3)[0]
            object_attach[p.name] = "lower" if ys.mean() > lm["waist"][1] else "chest"
            lm["twoHanded"] = True
            print(f"{p.name} held in both hands: goes with the body")
    if not lm.get("twoHanded") and hands_clasped(parts):
        lm["twoHanded"] = True
        print("hands clasped: both arms keep together")
    lm["faceHands"] = hands_at_face(parts)
    lm["bodyHands"] = [side for side in hands_on_body(parts) if side not in lm["faceHands"]]
    if "lock" in lm:
        lm["lockOnBody"] = lock_on_body(parts, lm)
        print(f"lock on the body: {lm['lockOnBody']:.2f} of it")
    if lm["faceHands"]:
        print(f"hands on the face keep still: {', '.join(lm['faceHands'])}")
    if lm["bodyHands"]:
        print(f"hands on the body sway less: {', '.join(lm['bodyHands'])}")
    if playing:
        # An instrument at the lips: the mouth stays as drawn under every face patch; only eyes and brows change.
        try:
            on_face = on_face | patches.mouth_area(parts, lm["face"], on_face.shape)
        except patches.Rejected:
            pass
    if on_face.any():
        print(f"features over an object on the face given to the face: {layers.features_into_face(parts, on_face)} px")

    if args.lama:
        from . import inpaint
        print(f"hidden paint inpainted: {inpaint.repaint_hidden(parts, args.lama)} px")

    loaded, rejected = [], []
    # A static object (a wheelchair with its stand) stays the base's: a gesture on its side swaps the arm alone, so no
    # second copy of the object fades in with it.
    held_side = None if hints.get("staticObjects") else object_side
    hidden = layers.ARM_TAGS if hints.get("staticObjects") else layers.HIDDEN_TAGS
    if args.variants:
        base_names = [p.name for p in parts]
        patch_base = None
        for spec in variants.load_manifest(args.variants):
            if spec["kind"] == "patch" and patch_base is None:
                patch_base = patches.Base(parts, reference, lm["face"], keep=on_face)
            try:
                v = variants.load(spec, reference, size, lm, held_side, base_names, patch_base, hidden)
            except patches.Rejected as e:
                rejected.append(spec["id"])
                print(f"variant {spec['id']:<11} rejected: {e}")
                continue
            except ValueError as e:
                if spec["kind"] != "arm":
                    raise
                # A split that drew both arms as one layer (a hand across the face center) gives no arm to swap.
                rejected.append(spec["id"])
                print(f"variant {spec['id']:<11} rejected: {e}")
                continue
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
                    parts, v.backdrop, f"backdrop.{v.side}@{v.id}", v.side, hidden=hidden)
                v.parts.insert(0, patch)
        filled, unknown = layers.fill_hidden(parts, backdrops, hidden=hidden)
        print(f"behind the arms: {filled} px repainted from variants, {unknown} px left as See-Through guessed")
        for v in loaded:
            v.backdrop = None
    rest = checks.rest_metrics(layers.composite(parts), reference, rest_ignore)
    problems = checks.rest_problems(rest)
    if problems:
        refuse(args.name, args.out, problems)
    bone_index = {b.name: i for i, b in enumerate(bones)}

    meshes, report = [], []
    body = layers.coverage([p for p in parts if p.tag in layers.BODY_TAGS + ("handwear", "hand")]) > 0.5
    outside = ndimage.distance_transform_edt(~body) if body.any() else None
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
        elif v is not None and v.kind == "patch":
            influences = patches.weights(vertices, layer.alpha, lm["faceWidth"])
        elif v is not None and v.kind == "arm":
            attach = f"forearm.{v.side}@{v.id}"
            influences = skeleton.weights_for(layer, vertices, lm, attach, arm=v.arm, suffix=f"@{v.id}")
        else:
            attach = object_attach.get(layer.name.split("@")[0]) if layer.tag == "objects" else None
            influences = skeleton.weights_for(layer, vertices, lm, attach)
            if layer.tag == "back hair" and "backHair" in lm and outside is not None:
                influences = skeleton.back_hair_on_body(influences, vertices, outside, lm)
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

    pose_name = args.band or (args.name.split("-", 1)[1] if args.name and "-" in args.name else None)
    swing = {**hints.get("swing", {}), **hints.get("poseSwing", {}).get(pose_name, {})}
    plist, drivers, physics = params.build(lm, list(bone_index), tail_swing=float(hints.get("tailSwing", 1.0)),
                                           swing={k: float(v) for k, v in swing.items()})
    vparams, vdrivers, part_drivers, vmeta = params.variant_controls(loaded, {e["name"] for e in entries})
    path = package.write(args.out, args.name, size, sheet, bones, plist + vparams, drivers + vdrivers, physics,
                         entries, landmark_summary(lm), part_drivers, vmeta)
    print("\n".join(report))
    for v in loaded:
        if v.kind == "patch":
            print(f"variant {v.id:<11} patch region={v.region} report={json.dumps(v.report)}")
            continue
        print(f"variant {v.id:<11} {v.kind:<4} side={v.side} front={v.front} parts={len(v.parts)} "
              f"registration={json.dumps(v.report)}")
    print(json.dumps({
        "package": str(path), "parts": len(entries), "bones": len(bones), "params": len(plist) + len(vparams),
        "variants": len(loaded), "rejected": rejected, "vertices": int(sum(len(e["vertices"]) // 2 for e in entries)),
        "atlas": list(sheet.shape[1::-1]), "rest_mean_abs_diff": rest["mean_abs_diff"],
        "rest_px_diff_gt_32_pct": rest["px_diff_gt_32_pct"],
    }))


if __name__ == "__main__":
    main()
