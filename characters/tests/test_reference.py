"""Reference player checks on a tiny synthetic rig (no art needed).

  python3 -m unittest characters.tests.test_reference
"""
import math
import unittest

import numpy as np

from characters.reference.motions import Director, Motion, band_of
from characters.reference.physics import Physics
from characters.reference.rig import Rig


def tiny_doc():
    square = [0, 0, 10, 0, 10, 10, 0, 10]
    return {
        "canvas": [100, 100],
        "params": [{"id": "Turn", "min": -1, "max": 1, "default": 0}, {"id": "Lift", "min": 0, "max": 1, "default": 0}],
        "bones": [
            {"name": "root", "parent": None, "pivot": [0, 0]},
            {"name": "arm", "parent": "root", "pivot": [0, 0]},
            {"name": "hand", "parent": "arm", "pivot": [10, 0]},
        ],
        "drivers": [
            {"param": "Turn", "bone": "arm", "channel": "rotate", "keys": [[-1, -90], [1, 90]]},
            {"param": "Lift", "bone": "hand", "channel": "y", "keys": [[0, 0], [1, -5]]},
        ],
        "physics": [],
        "parts": [
            {"name": "a", "tag": "topwear", "order": 0, "vertices": square, "uv": square, "triangles": [0, 1, 2, 0, 2, 3],
             "influences": 2, "bones": [1, 2] * 4, "weights": [1, 0, 0, 1, 0, 1, 1, 0]},
        ],
    }


class RigTests(unittest.TestCase):
    def test_defaults_leave_the_mesh_in_place(self):
        rig = Rig(tiny_doc())
        np.testing.assert_allclose(rig.pose(rig.defaults())[0], rig.parts[0]["vertices"], atol=1e-9)

    def test_rotation_turns_around_the_pivot_clockwise_on_screen(self):
        rig = Rig(tiny_doc())
        verts = rig.pose({"Turn": 1})[0]
        np.testing.assert_allclose(verts[1], [0, 10], atol=1e-9)

    def test_child_bones_inherit_and_weights_blend(self):
        rig = Rig(tiny_doc())
        verts = rig.pose({"Turn": 1, "Lift": 1})[0]
        np.testing.assert_allclose(verts[1], [5, 10], atol=1e-9)
        np.testing.assert_allclose(verts[0], [0, 0], atol=1e-9)

    def test_values_are_clamped_to_the_parameter_range(self):
        rig = Rig(tiny_doc())
        np.testing.assert_allclose(rig.pose({"Turn": 5})[0], rig.pose({"Turn": 1})[0], atol=1e-9)

    def test_parents_must_come_first(self):
        doc = tiny_doc()
        doc["bones"].reverse()
        with self.assertRaises(ValueError):
            Rig(doc)


class VariantTests(unittest.TestCase):
    def test_part_opacity_is_the_product_of_its_drivers(self):
        doc = tiny_doc()
        doc["params"] += [{"id": "Var.a", "min": 0, "max": 1, "default": 0}, {"id": "Var.b", "min": 0, "max": 1, "default": 0}]
        doc["partDrivers"] = [{"param": "Var.a", "part": "a", "keys": [[0, 1], [1, 0]]},
                              {"param": "Var.b", "part": "a", "keys": [[0, 1], [1, 0]]}]
        rig = Rig(doc)
        self.assertAlmostEqual(rig.opacities({})[0], 1.0)
        self.assertAlmostEqual(rig.opacities({"Var.a": 0.5, "Var.b": 0.5})[0], 0.25)

    def test_arm_weights_follow_the_nearest_segment_even_when_folded(self):
        from characters.rig.skeleton import arm_from_joints, arm_weights
        arm = arm_from_joints({"shoulder": (0, 0), "elbow": (10, 100), "wrist": (40, 10)})
        chest, upper, fore = arm_weights(np.array([[1.0, 2.0], [6.0, 60.0], [38.0, 20.0], [50.0, -20.0]]), arm)
        np.testing.assert_allclose(chest + upper + fore, 1, atol=1e-9)
        self.assertGreater(chest[0], 0.9)
        self.assertGreater(upper[1], 0.9)
        self.assertGreater(fore[2], 0.9)
        self.assertGreater(fore[3], 0.9)

    def test_face_variants_split_eyes_from_mouth(self):
        from characters.rig.layers import Layer
        from characters.rig.params import variant_controls
        from characters.rig.variants import Variant
        parts = [Layer("eyelash.left@smile", "eyelash", None, "left"), Layer("mouth@smile", "mouth", None)]
        v = Variant("smile", "face", None, parts, ["eyelash.left", "mouth"])
        params, _, part_drivers, meta = variant_controls([v], {"eyelash.left", "mouth", "eyelash.left@smile", "mouth@smile"})
        by_part = {d["part"]: d["param"] for d in part_drivers}
        self.assertEqual(by_part["mouth@smile"], "Mouth.smile")
        self.assertEqual(by_part["mouth"], "Mouth.smile")
        self.assertEqual(by_part["eyelash.left"], "Var.smile")
        self.assertTrue(meta[0]["eyesClosed"])
        self.assertEqual({p["id"] for p in params}, {"Var.smile", "Mouth.smile"})

    def test_arm_swap_stays_nearly_opaque(self):
        from characters.rig.layers import Layer
        from characters.rig.params import variant_controls
        from characters.rig.variants import Variant
        v = Variant("wave", "arm", "right", [Layer("handwear.right@wave", "handwear", None, "right")],
                    ["handwear.right"])
        _, _, part_drivers, _ = variant_controls([v], {"handwear.right", "handwear.right@wave"})
        keys = {d["part"]: np.array(d["keys"], float) for d in part_drivers}
        for t in np.linspace(0, 1, 41):
            new = np.interp(t, keys["handwear.right@wave"][:, 0], keys["handwear.right@wave"][:, 1])
            old = np.interp(t, keys["handwear.right"][:, 0], keys["handwear.right"][:, 1])
            self.assertGreaterEqual(1 - (1 - new) * (1 - old), 0.95)

    def test_old_and_new_arm_meet_halfway(self):
        from characters.rig.variants import _meet
        for gap in (10.0, -40.0, 100.0, 170.0):
            lead, approach = _meet(gap, 90, 90)
            self.assertAlmostEqual(gap + lead - approach, 0.0)
            self.assertLessEqual(abs(lead), 90)
            self.assertLessEqual(abs(approach), 90)
        lead, approach = _meet(200.0, 60, 60)
        self.assertEqual((lead, approach), (-60.0, 60.0))


class FillHiddenTests(unittest.TestCase):
    def scene(self):
        from characters.rig.layers import Layer
        h = w = 40

        def layer(name, tag, color, box):
            rgba = np.zeros((h, w, 4), np.float32)
            x0, y0, x1, y1 = box
            rgba[y0:y1, x0:x1, :3] = color
            rgba[y0:y1, x0:x1, 3] = 1
            return Layer(name, tag, rgba)

        body = layer("topwear", "topwear", (0.2, 0.2, 0.2), (5, 5, 35, 35))
        arm = layer("handwear.right", "handwear", (0.9, 0.1, 0.1), (20, 10, 30, 30))
        owner = np.full((h, w), -1, np.int32)
        owner[2:38, 2:38] = 0
        backdrop = {"names": ["topwear"], "tags": ["topwear"], "owner": owner,
                    "rgb": np.full((h, w, 3), 0.6, np.float32), "coverage": (owner >= 0).astype(np.float32),
                    "hidden": np.zeros((h, w), np.float32)}
        return [body, arm], backdrop

    def test_fill_repaints_only_what_the_arm_covers(self):
        from characters.rig.layers import composite, fill_hidden
        parts, backdrop = self.scene()
        before = composite(parts)
        filled, left = fill_hidden(parts, [backdrop], guard=0)
        np.testing.assert_allclose(composite(parts), before)
        self.assertEqual((filled, left), (200, 0))
        np.testing.assert_allclose(parts[0].rgba[20, 25, :3], 0.6)
        np.testing.assert_allclose(parts[0].rgba[20, 10, :3], 0.2)

    def test_patch_weights_follow_the_owner_layer(self):
        from characters.rig.skeleton import weights_by_owner
        tags = np.full((40, 40), "topwear", object)
        tags[:, 20:] = "face"
        lm = {"face": (0, 0, 10, 10), "chin": 10, "waist": (20, 30)}
        influences = dict(weights_by_owner(np.array([[5.0, 5.0], [30.0, 5.0]]), tags.astype(str), lm))
        np.testing.assert_allclose(influences["chest"], [1, 0])
        np.testing.assert_allclose(influences["face"], [0, 1])


class PhysicsTests(unittest.TestCase):
    def spec(self, wind=1.0):
        return {"name": "hair", "anchor": [0, 0], "segments": [100, 100], "inputs": [{"param": "Sway", "x": 50}],
                "outputs": [{"param": "H1", "segment": 0, "degrees": 10}, {"param": "H2", "segment": 1, "degrees": 10}],
                "stiffness": 30, "damping": 6, "wind": wind}

    def test_still_air_settles_straight_down(self):
        p = Physics([self.spec()], wind_sign=1.0)
        p.reset({})
        for _ in range(240):
            out = p.step(1 / 60, {})
        self.assertAlmostEqual(out["H1"], 0, places=4)

    def test_wind_streams_downwind_and_stays_bounded(self):
        p = Physics([self.spec()], wind_sign=1.0)
        p.reset({})
        for _ in range(300):
            out = p.step(1 / 60, {"Wind": 1})
        self.assertGreater(out["H1"], 0.2)
        self.assertLess(abs(out["H2"]), 1)

    def test_moving_the_anchor_makes_the_hair_lag(self):
        p = Physics([self.spec(wind=0)], wind_sign=1.0)
        p.reset({})
        out = p.step(1 / 60, {"Sway": 1})
        self.assertLess(out["H1"], 0)


class MotionTests(unittest.TestCase):
    def test_motion_curves_ease_between_keys_and_loop(self):
        m = Motion("m", 2.0, {"Turn": [(0, 0), (1, 1), (2, 0)]}, loop=True)
        self.assertAlmostEqual(m.sample(0.5)["Turn"], 0.5)
        self.assertAlmostEqual(m.sample(2.5)["Turn"], 0.5)

    def test_curves_keep_holds_flat_and_never_overshoot(self):
        from characters.reference.motions import Curve
        c = Curve([(0, 0), (1, 1), (2, 1), (3, 0.2), (4, 0.6)])
        xs = np.linspace(0, 4, 401)
        ys = np.array([c(x) for x in xs])
        np.testing.assert_allclose(ys[(xs >= 1) & (xs <= 2)], 1.0, atol=1e-9)
        self.assertLessEqual(ys.max(), 1.0 + 1e-9)
        self.assertGreaterEqual(ys.min(), -1e-9)
        self.assertTrue(np.all(np.diff(ys[(xs >= 2) & (xs <= 3)]) <= 1e-12))

    def test_springs_overshoot_then_settle_and_snap_in_place(self):
        from characters.reference.motions import SecondOrder
        s = SecondOrder(2.6, 0.55, 0.0)
        ys = [s.update(1 / 60, 1.0) for _ in range(240)]
        self.assertGreater(max(ys), 1.02)
        self.assertAlmostEqual(ys[-1], 1.0, places=3)
        s.snap(0.3)
        self.assertAlmostEqual(s.update(1 / 60, 0.3), 0.3)

    def test_moves_start_and_end_at_rest(self):
        from characters.reference.motions import MOVES
        for name, build in MOVES.items():
            m = build("right", "left")
            for pid, v in m.sample(0.0).items():
                self.assertAlmostEqual(v, 0.0, msg=f"{name} {pid} start")
            for pid, v in m.sample(m.duration).items():
                if pid != "Spin":
                    self.assertAlmostEqual(v, 0.0, msg=f"{name} {pid} end")

    def test_bands_follow_the_levels(self):
        self.assertEqual([band_of(n) for n in (0, 1, 5, 9, 12, 13)],
                         ["idle", "cruise", "boost", "overdrive", "redline", "redline"])

    def test_director_keeps_a_full_body_rig_in_range_and_near_its_spot(self):
        params = {}
        for pid in ("AngleX", "AngleY", "AngleZ", "BodyAngleZ", "BodySway", "EyeBallX", "EyeBallY", "PosX", "PosY",
                    "Squash", "Lean", "Hip", "Arm.left", "Arm.right", "Forearm.left", "Forearm.right",
                    "Shoulder.left", "Shoulder.right"):
            params[pid] = {"id": pid, "min": -1, "max": 1, "default": 0}
        for pid in ("Breath", "Wind", "Spin", "SkirtFlare", "FootLift", "FootPoint"):
            params[pid] = {"id": pid, "min": 0, "max": 1, "default": 0}
        d = Director(params, arm_out="right", arm_book="left", orb=-1.0)
        played = set()
        for n in range(60 * 150):
            d.set_level(min(13, (n // 600) % 15))
            values = d.update(1 / 60)
            played.add(d.last_gesture)
            self.assertTrue(all(math.isfinite(v) for v in values.values()))
            self.assertLessEqual(abs(values["PosX"]), 1.2)
            self.assertGreaterEqual(d.stage_x, -1e-9)
        self.assertTrue({"hop", "spin", "step", "sway"} & played)

    def test_director_output_stays_inside_parameter_ranges(self):
        rig = Rig(tiny_doc())
        params = dict(rig.params)
        for pid in ("AngleX", "AngleY", "AngleZ", "EyeBallX", "EyeBallY", "BodySway", "Arm.right", "Forearm.right",
                    "Arm.left", "Forearm.left", "BodyAngleZ"):
            params[pid] = {"id": pid, "min": -1, "max": 1, "default": 0}
        for pid in ("Breath", "Wind"):
            params[pid] = {"id": pid, "min": 0, "max": 1, "default": 0}
        for pid in ("EyeOpen.left", "EyeOpen.right"):
            params[pid] = {"id": pid, "min": 0, "max": 1, "default": 1}
        d = Director(params, arm_out="right", arm_book="left")
        for n in range(60 * 30):
            d.set_level(min(13, n // 120))
            values = d.update(1 / 60)
            self.assertTrue(all(math.isfinite(v) for v in values.values()))
            self.assertTrue(0 <= values["Wind"] <= 1)
            self.assertTrue(0 <= values["EyeOpen.left"] <= 1)


if __name__ == "__main__":
    unittest.main()


class HandTests(unittest.TestCase):
    def hand(self):
        from characters.rig import hands
        pts = [(50, 100)]
        for i, x in enumerate((30, 40, 50, 60, 70)):
            pts += [(x, 90 - 10 * k) for k in range(4)]
        return dict(hands.landmarks(np.array(pts, float), (50, 160)), side="right")

    def test_hand_weights_sum_to_one_and_follow_the_forearm_at_the_cut(self):
        from characters.rig import hands
        h = self.hand()
        verts = np.array([[50.0, 104.0], [50.0, 60.0], [70.0, 62.0]])
        influences = dict(hands.weights(verts, h))
        total = sum(influences.values())
        np.testing.assert_allclose(total, 1, atol=1e-9)
        self.assertGreater(influences["forearm.right"][0], 0.9)
        self.assertGreater(influences["finger.right.2.1"][1] + influences["finger.right.2.2"][1], 0.6)

    def test_split_moves_the_hand_out_of_the_arm_layer(self):
        from characters.rig import hands
        from characters.rig.layers import Layer
        h = self.hand()
        rgba = np.zeros((200, 200, 4), np.float32)
        rgba[40:180, 25:75] = (1, 1, 1, 1)
        arm, part = hands.split(Layer("handwear.right", "handwear", rgba, "right"), h)
        self.assertEqual(arm.alpha[60, 50], 0)
        self.assertEqual(part.alpha[60, 50], 1)
        self.assertEqual(arm.alpha[170, 50], 1)
        self.assertEqual(part.alpha[170, 50], 0)

    def test_finger_curl_turns_toward_the_palm(self):
        from characters.rig.params import hand_drivers
        h = self.hand()
        drivers = hand_drivers("right", h)
        curl = [d for d in drivers if d["param"] == "Finger.right.2" and d["channel"] == "rotate"]
        self.assertEqual(len(curl), 3)
        self.assertTrue(all(d["keys"][-1][1] != 0 for d in curl))


class StageTests(unittest.TestCase):
    def test_odometer_holds_its_value_between_samples(self):
        from characters.reference.hud import Odometer
        odo = Odometer()
        seen = set()
        for k in range(60):
            seen.add(odo.update(1 / 60, 100 + k))
        self.assertLessEqual(len(seen), 10)

    def test_switch_swaps_only_when_edge_on(self):
        import math as m
        from characters.reference.stage import Switch
        s = Switch()
        s.request("next")
        swapped_at = None
        for k in range(60):
            values = {}
            out = s.update(1 / 60, values)
            if out is not None:
                swapped_at = values["Spin"]
            if k > 40:
                self.assertEqual(values.get("Spin", 0.0), 0.0)
        self.assertIsNotNone(swapped_at)
        self.assertLess(abs(m.cos(2 * m.pi * swapped_at)), 0.2)
