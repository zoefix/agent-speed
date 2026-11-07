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
        m = Motion("m", 2.0, {"Turn": [(0, 0), (1, 1), (2, 0)]})
        self.assertAlmostEqual(m.sample(0.5)["Turn"], 0.5)
        self.assertAlmostEqual(m.sample(2.5)["Turn"], 0.5)

    def test_bands_follow_the_levels(self):
        self.assertEqual([band_of(n) for n in (0, 1, 5, 9, 12, 13)],
                         ["idle", "cruise", "boost", "overdrive", "redline", "redline"])

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
