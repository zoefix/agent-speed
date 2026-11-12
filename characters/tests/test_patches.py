"""Face patch checks on a synthetic face (no art needed).

  python3 -m unittest characters.tests.test_patches
"""
import tempfile
import unittest
from pathlib import Path

import cv2
import numpy as np
from PIL import Image
from scipy import ndimage

from characters.rig import patches
from characters.rig.layers import Layer, composite
from characters.rig.params import variant_controls
from characters.rig.variants import Variant

SIZE = 400
CENTER, RADIUS = (200, 200), 60
SKIN, INK = (0.97, 0.88, 0.84), (0.25, 0.15, 0.12)


def layer(name, tag, mask, color, side=None):
    rgba = np.zeros((SIZE, SIZE, 4), np.float32)
    rgba[..., :3] = color
    rgba[..., 3] = mask
    return Layer(name, tag, rgba, side)


def ellipse(cx, cy, rx, ry):
    yy, xx = np.mgrid[:SIZE, :SIZE]
    return (((xx - cx) / rx) ** 2 + ((yy - cy) / ry) ** 2 <= 1).astype(np.float32)


def box(x0, y0, x1, y1):
    out = np.zeros((SIZE, SIZE), np.float32)
    out[y0:y1, x0:x1] = 1
    return out


def texture():
    """Paper with blotches and strokes: something for SIFT to hold on to around the face."""
    rng = np.random.default_rng(3)
    noise = ndimage.gaussian_filter(rng.random((SIZE, SIZE, 3)).astype(np.float32), (3, 3, 0))
    img = 0.25 + 1.5 * (noise - noise.mean())
    for _ in range(140):
        x, y, r = rng.integers(0, SIZE, 2).tolist() + [int(rng.integers(3, 12))]
        cv2.circle(img, (x, y), r, rng.random(3).tolist(), -1)
        x2, y2 = rng.integers(0, SIZE, 2).tolist()
        cv2.line(img, (x, y), (x2, y2), rng.random(3).tolist(), 2)
    return np.clip(img, 0, 1)


def face_layers():
    cx, cy = CENTER
    return [
        layer("backdrop", "back hair", np.ones((SIZE, SIZE), np.float32), (0.5, 0.5, 0.5)),
        layer("face", "face", ellipse(cx, cy, RADIUS, RADIUS), SKIN),
        layer("eyewhite.left", "eyewhite", ellipse(175, 192, 10, 6), (1, 1, 1), "left"),
        layer("eyewhite.right", "eyewhite", ellipse(225, 192, 10, 6), (1, 1, 1), "right"),
        layer("irides.left", "irides", ellipse(175, 192, 4, 5), (0.6, 0.35, 0.1), "left"),
        layer("irides.right", "irides", ellipse(225, 192, 4, 5), (0.6, 0.35, 0.1), "right"),
        layer("eyebrow.left", "eyebrow", box(163, 176, 187, 180), INK, "left"),
        layer("eyebrow.right", "eyebrow", box(213, 176, 237, 180), INK, "right"),
        layer("nose", "nose", box(199, 208, 202, 214), INK),
        layer("mouth", "mouth", box(190, 228, 210, 231), INK),
        layer("front hair", "front hair", box(130, 130, 270, 165), (0.9, 0.45, 0.15)),
    ]


def scene():
    """Layers of a face on textured paper (the paper drawn in the backdrop layer) and the art they compose."""
    parts = face_layers()
    parts[0].rgba[..., :3] = texture()
    return parts, composite(parts)


def face_box(parts):
    ys, xs = np.nonzero(parts[1].alpha > 0.12)
    return float(xs.min()), float(ys.min()), float(xs.max() + 1), float(ys.max() + 1)


def save_edit(path, image, scale=1.0, shift=(0.0, 0.0), out_size=SIZE):
    """Writes the image moved by a similarity about the center and resampled to `out_size` square, as a generated
    edit would come back."""
    c = SIZE / 2
    m = np.array([[scale, 0, c - scale * c + shift[0]], [0, scale, c - scale * c + shift[1]]], np.float64)
    m *= out_size / SIZE
    moved = cv2.warpAffine(image, m, (out_size, out_size), flags=cv2.INTER_CUBIC, borderMode=cv2.BORDER_REFLECT)
    Image.fromarray((np.clip(moved, 0, 1) * 255).round().astype(np.uint8)).save(path)


class RegionTests(unittest.TestCase):
    def setUp(self):
        self.parts, self.art = scene()
        self.base = patches.Base(self.parts, self.art, face_box(self.parts))

    def test_face_region_keeps_the_jaw_line_and_covers_every_feature(self):
        r = self.base.region("face")
        cx, cy = CENTER
        self.assertFalse(r["mask"][cy + RADIUS - 2, cx])
        self.assertTrue(r["mask"][cy + RADIUS - 6, cx])
        for name in ("eyewhite.left", "irides.right", "eyebrow.left", "nose", "mouth"):
            part = next(p for p in self.parts if p.name == name)
            self.assertTrue(r["mask"][part.alpha > 0.05].all(), name)
        self.assertEqual(sorted(r["replaces"]), sorted(
            ["eyewhite.left", "eyewhite.right", "irides.left", "irides.right", "eyebrow.left", "eyebrow.right",
             "nose", "mouth"]))

    def test_mouth_region_spans_the_mouth_from_below_the_nose_to_above_the_chin(self):
        r = self.base.region("mouth")
        fw = self.base.fw
        mask = r["mask"]
        self.assertTrue(mask[229, int(200 + 0.2 * fw)] and mask[229, int(200 - 0.2 * fw)])
        self.assertFalse(mask[229, int(200 + 0.26 * fw)])
        self.assertTrue(mask[220, 200])
        self.assertFalse(mask[212, 200])
        self.assertFalse(mask[192, 175])
        self.assertTrue(mask[CENTER[1] + RADIUS - 8, 200])
        self.assertFalse(mask[CENTER[1] + RADIUS - 2, 200])
        self.assertEqual(r["replaces"], ["mouth"])

    def test_eye_region_grows_the_eyes_and_leaves_the_brows(self):
        r = self.base.region("eyes")
        mask = r["mask"]
        self.assertTrue(mask[192, 175 + 10 + 4])
        self.assertTrue(mask[192 + 6 + 3, 225])
        self.assertTrue(mask[183, 175])
        self.assertFalse(mask[178, 175])
        self.assertFalse(mask[229, 200])
        self.assertEqual(sorted(r["replaces"]),
                         ["eyewhite.left", "eyewhite.right", "irides.left", "irides.right"])

    def test_feather_crosses_half_on_the_edge(self):
        mask = box(100, 100, 300, 300).astype(bool)
        alpha = patches.feather(mask, 1.5)
        self.assertAlmostEqual(alpha[200, 100], 2 / 3, places=3)
        self.assertAlmostEqual(alpha[200, 99], 1 / 3, places=3)
        self.assertEqual(alpha[200, 102], 1.0)
        self.assertEqual(alpha[200, 97], 0.0)

    def test_the_bangs_keep_the_base_face_under_them(self):
        parts = face_layers()
        art = composite(parts)
        base = patches.Base(parts, art, face_box(parts))
        r = base.region("face")
        np.testing.assert_allclose(r["under"][150, 200], SKIN, atol=1e-6)
        self.assertGreater(r["cover"][150, 200], 0.99)
        self.assertLess(r["cover"][229, 200], 0.01)


class AlignTests(unittest.TestCase):
    def setUp(self):
        self.parts, self.art = scene()
        self.base = patches.Base(self.parts, self.art, face_box(self.parts))
        self.tmp = tempfile.TemporaryDirectory()

    def tearDown(self):
        self.tmp.cleanup()

    def test_a_shifted_and_scaled_edit_lands_on_the_base(self):
        path = Path(self.tmp.name) / "edit.png"
        save_edit(path, self.art, scale=1.05, shift=(9.0, -6.0), out_size=460)
        keep_out = patches.grow(self.base.region("mouth")["mask"], 4)
        aligned, report = patches.align(path, self.art, (SIZE, SIZE), self.base.head, keep_out)
        self.assertLess(report["head"]["residual_px"], 0.6)
        self.assertAlmostEqual(report["whole"]["scale"], 1 / 1.05, delta=0.005)
        x0, y0, x1, y1 = (int(v) for v in self.base.head)
        diff = np.abs(aligned - self.art)[y0 + 10:y1 - 10, x0 + 10:x1 - 10]
        self.assertLess(float(diff.mean()), 0.03)

    def test_a_changed_mouth_becomes_a_patch_and_an_unchanged_one_is_rejected(self):
        edited = [Layer(p.name, p.tag, p.rgba.copy(), p.side) for p in self.parts]
        mouth = next(p for p in edited if p.tag == "mouth")
        mouth.rgba[..., 3] = ellipse(200, 232, 9, 6)
        mouth.rgba[..., :3] = (0.55, 0.12, 0.15)
        path = Path(self.tmp.name) / "open.png"
        save_edit(path, composite(edited), scale=0.97, shift=(-5.0, 4.0), out_size=380)
        rgba, replaces, report = patches.cut(self.base, path, "mouth")
        self.assertEqual(replaces, ["mouth"])
        self.assertGreater(report["changed_px"], 100)
        self.assertGreater(rgba[232, 200, 3], 0.99)
        np.testing.assert_allclose(rgba[232, 200, :3], (0.55, 0.12, 0.15), atol=0.08)
        self.assertEqual(rgba[150, 200, 3], 0.0)

        same = Path(self.tmp.name) / "same.png"
        save_edit(same, self.art, scale=0.97, shift=(-5.0, 4.0), out_size=380)
        with self.assertRaisesRegex(patches.Rejected, "barely changed"):
            patches.cut(self.base, same, "mouth")

    def test_an_edit_of_another_picture_is_rejected(self):
        path = Path(self.tmp.name) / "other.png"
        other = np.random.default_rng(9).random((SIZE, SIZE, 3)).astype(np.float32)
        save_edit(path, other)
        with self.assertRaisesRegex(patches.Rejected, "registration failed"):
            patches.cut(self.base, path, "face")


class PatchRigTests(unittest.TestCase):
    def test_patch_weights_follow_the_face_at_the_edge_and_the_features_inside(self):
        alpha = patches.feather(box(100, 100, 300, 300).astype(bool))
        (b0, inner), (b1, outer) = patches.weights(np.array([[100.0, 200.0], [200.0, 200.0]]), alpha, 100)
        self.assertEqual((b0, b1), ("features", "face"))
        np.testing.assert_allclose(inner + outer, 1)
        self.assertLess(inner[0], 0.05)
        self.assertGreater(inner[1], 0.99)

    def test_a_patch_is_fully_in_by_halfway_and_the_features_it_replaces_fade_out(self):
        v = Variant("smile", "patch", None, [Layer("face@smile", "face", None)], ["mouth", "eyebrow.left"],
                    region="mouth")
        params, _, part_drivers, meta = variant_controls([v], {"face@smile", "mouth", "eyebrow.left"})
        keys = {d["part"]: d["keys"] for d in part_drivers}
        self.assertEqual(keys["face@smile"], [[0, 0], [0.5, 1]])
        self.assertEqual(keys["mouth"], [[0, 1], [1, 0]])
        self.assertEqual({d["param"] for d in part_drivers}, {"Var.smile"})
        self.assertEqual(meta, [{"id": "smile", "kind": "patch", "param": "Var.smile", "region": "mouth"}])
        self.assertEqual([p["id"] for p in params], ["Var.smile"])


class ReelTests(unittest.TestCase):
    def test_each_variant_eases_in_holds_and_eases_out_in_turn(self):
        from characters.reference import variants_reel as reel
        slots, seconds = reel.schedule(["a", "b"])
        (_, a), (_, b) = slots
        span = 2 * reel.EASE + reel.HOLD
        self.assertAlmostEqual(b - a, span + reel.GAP)
        self.assertAlmostEqual(seconds, b + span + reel.GAP)
        self.assertEqual(reel.weight(a, a), 0.0)
        self.assertAlmostEqual(reel.weight(a + reel.EASE / 2, a), 0.5)
        self.assertEqual(reel.weight(a + reel.EASE + reel.HOLD / 2, a), 1.0)
        self.assertEqual(reel.weight(a + span + 0.01, a), 0.0)
        self.assertEqual(reel.weight(b + 0.5, a), 0.0)


if __name__ == "__main__":
    unittest.main()


class PatchOrderTests(unittest.TestCase):
    def test_patches_lie_above_the_face_eyes_over_mouths_over_expressions(self):
        from characters.rig.build import ordered_parts
        base = face_layers()
        loaded = [Variant(vid, "patch", None, [layer(f"face@{vid}", "face", ellipse(*CENTER, 50, 50), SKIN)],
                          ["mouth"], region=region)
                  for vid, region in (("gaze-left", "eyes"), ("mouth-a", "mouth"), ("expr-smile", "face"))]
        names = [layer_.name for layer_, _ in ordered_parts(base, loaded)]
        face = names.index("face")
        self.assertEqual(names[face + 1:face + 4], ["face@expr-smile", "face@mouth-a", "face@gaze-left"])
        self.assertEqual(names[face + 4], base[2].name)


class PackStateTests(unittest.TestCase):
    def test_a_pack_takes_only_known_mood_poses(self):
        from characters.rig import pack
        spec = {"id": "x", "packVersion": 1, "name": {}, "vendors": [], "accent": "#000000"}
        with tempfile.TemporaryDirectory() as out:
            with self.assertRaises(ValueError):
                pack.build(spec, {"idle": out}, out, {"nap": out})
