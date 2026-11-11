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
    def test_a_jump_with_tucked_legs_keeps_its_size_by_the_middle_ratio(self):
        from characters.reference.stage import pose_scale
        self.assertAlmostEqual(pose_scale((70, 28, 50), (40, 28, 48)), 50 / 48)
        self.assertAlmostEqual(pose_scale((70, 28, None), (40, 28, None)), 70 / 40)

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


class OrbitTests(unittest.TestCase):
    def test_kepler_orbit_moves_faster_near_the_center(self):
        from characters.reference.orbits import eccentric_anomaly
        e = 0.4

        def plane(mean):
            E = eccentric_anomaly(mean, e)
            return np.array([math.cos(E) - e, math.sqrt(1 - e * e) * math.sin(E)])

        near = np.linalg.norm(plane(0.01) - plane(0.0))
        far = np.linalg.norm(plane(math.pi + 0.01) - plane(math.pi))
        self.assertAlmostEqual(near / far, (1 + e) / (1 - e), places=2)
        for mean in (0.3, 2.0, 5.5):
            E = eccentric_anomaly(mean, e)
            self.assertAlmostEqual(E - e * math.sin(E), mean, places=9)

    def test_orbit_passes_in_front_and_behind(self):
        from characters.reference.orbits import Orbit
        o = Orbit(100.0, 0.2, 10.0, 0.0, 0.0, 0.0)
        depths = [o.at(t / 10, 0, 0)[2] for t in range(100)]
        self.assertLess(min(depths), -0.8)
        self.assertGreater(max(depths), 0.8)


class FixtureTests(unittest.TestCase):
    def test_shared_fixtures_match_the_reference_player(self):
        from characters.tests.make_fixtures import main
        self.assertEqual(main(["--check"]), 0, "run python3 -m characters.tests.make_fixtures")


def body_params(*extra):
    params = {}
    for pid in ("AngleX", "AngleY", "AngleZ", "BodyAngleZ", "BodySway", "EyeBallX", "EyeBallY", "PosX", "PosY",
                "Squash", "Lean", "Hip", "Arm.left", "Arm.right", "Forearm.left", "Forearm.right", "Shoulder.left",
                "Shoulder.right") + extra:
        params[pid] = {"id": pid, "min": -1, "max": 1, "default": 0}
    for pid in ("Breath", "Wind", "Spin", "SkirtFlare", "FootLift", "FootPoint"):
        params[pid] = {"id": pid, "min": 0, "max": 1, "default": 0}
    return params


class ProfileTests(unittest.TestCase):
    def play(self, profile, seconds=150, extra=()):
        d = Director(body_params(*extra), arm_out="right", arm_book="left", orb=-1.0, profile=profile)
        played, frames = set(), []
        for n in range(60 * seconds):
            d.set_level(min(13, (n // 600) % 15))
            frames.append(d.update(1 / 60))
            played.add(d.last_gesture)
        return played, frames

    def test_both_hands_grip_an_object_held_up_and_the_arms_keep_their_pose(self):
        fingers = tuple(f"Finger.{side}.{i}" for side in ("left", "right") for i in range(5))
        d = Director(body_params(*fingers), arm_out="right", arm_book="left", orb=-1.0,
                     profile={"poseHands": {"idle": "grip"}})
        played = set()
        for n in range(60 * 20):
            d.set_level(0)
            values = d.update(1 / 60)
            for side in ("left", "right"):
                self.assertEqual(values[f"Arm.{side}"], 0.0)
            played |= {h.action.name for h in d.hands if h.action is not None}
        self.assertEqual(played, {"grip"})

    def test_the_head_keeps_still_while_a_flute_is_at_the_lips(self):
        fingers = tuple(f"Finger.{side}.{i}" for side in ("left", "right") for i in range(5))
        _, frames = self.play({"poseHands": {"idle": "flute"}}, seconds=20, extra=fingers)
        moved = max(abs(f.get(pid, 0.0)) for f in frames[:600] for pid in ("AngleX", "AngleY", "AngleZ"))
        self.assertLess(moved, 1e-9)
        _, frames = self.play({}, seconds=20, extra=fingers)
        self.assertGreater(max(abs(f.get("AngleX", 0.0)) for f in frames[:600]), 0.01)

    def test_without_a_profile_the_pools_are_claudes(self):
        from characters.reference.motions.director import POOLS
        from characters.reference.motions.profile import MotionProfile
        for band, base in POOLS.items():
            self.assertEqual(MotionProfile().pool(base, band), list(base))
            self.assertEqual(MotionProfile({}).pool(base, band), list(base))

    def test_signature_actions_weigh_three_times_inside_their_bands(self):
        from characters.reference.motions.director import POOLS
        from characters.reference.motions.profile import MotionProfile
        p = MotionProfile({"signature": ["tailswish", "yawn", "spin", "nonsense"], "exclude": ["run"]})
        self.assertEqual(p.pool(POOLS["redline"], "redline").count("tailswish"), 3)
        self.assertEqual(p.pool(POOLS["redline"], "redline").count("spin"), 3)
        self.assertNotIn("yawn", p.pool(POOLS["redline"], "redline"))
        self.assertEqual(p.pool(POOLS["idle"], "idle").count("yawn"), 3)
        self.assertNotIn("run", p.pool(POOLS["overdrive"], "overdrive"))
        self.assertEqual(p.signature, ["tailswish", "yawn", "spin"])

    def test_numbers_are_clamped(self):
        from characters.reference.motions.profile import MotionProfile
        p = MotionProfile({"tempo": 0, "gap": -3, "energy": 99, "sway": "fast", "bounce": float("nan")})
        self.assertEqual((p.tempo, p.gap, p.energy, p.sway, p.bounce), (0.25, 0.25, 4.0, 1.0, 1.0))

    def test_a_seated_character_keeps_her_seat(self):
        from characters.reference.motions.profile import SEATED_FREEZE, SEATED_SKIP
        played, frames = self.play({"seated": True, "signature": ["spin", "nod", "hop"]})
        self.assertFalse(played & SEATED_SKIP)
        self.assertIn("nod", played)
        for values in frames:
            for pid in SEATED_FREEZE:
                if pid in values:
                    self.assertEqual(values[pid], 0.0, pid)

    def test_a_silent_character_never_hums_or_giggles(self):
        played, _ = self.play({"talk": False, "signature": ["hum", "giggle", "nod"]})
        self.assertFalse(played & {"hum", "giggle"})
        self.assertIn("nod", played)

    def test_tail_wings_and_ears_move_and_stay_in_range(self):
        extra = ("TailSwish", "WingFlap", "EarTwitch.left", "EarTwitch.right")
        played, frames = self.play({"signature": ["tailswish", "wingflap", "eartwitch", "pounce", "floatbob"]},
                                   extra=extra)
        self.assertTrue({"tailswish", "wingflap", "eartwitch"} & played)
        for pid in extra:
            series = [v.get(pid, 0.0) for v in frames]
            self.assertGreater(max(series) - min(series), 0.05, pid)
            # Only the springs' follow-through may pass the range a little; the rig clamps it.
            self.assertTrue(all(abs(v) <= 1.1 and math.isfinite(v) for v in series), pid)

    def test_a_slower_tempo_slows_the_beat(self):
        from characters.reference.motions.rhythm import Beat
        slow, fast = Beat(0.5), Beat(1.0)
        for _ in range(600):
            slow.update(1 / 60, "boost")
            fast.update(1 / 60, "boost")
        self.assertAlmostEqual(slow.bpm / fast.bpm, 0.5, places=6)


class StretchTests(unittest.TestCase):
    """Squash stretches (+) or squashes (-) her whole figure over the feet, face included: past HELD either way it
    reads as the art drawn out or pressed flat, so only an instant of a take-off or a landing goes there, and the
    crouch before a leap no longer than CROUCH (spec/CHARACTERS.md, Motion)."""

    HELD, BRIEF, CROUCH = 0.2, 0.25, 0.5

    def longest(self, series, dt, sign=1.0):
        run = longest = 0.0
        for v in series:
            run = run + dt if sign * v > self.HELD + 1e-9 else 0.0
            longest = max(longest, run)
        return longest

    def assert_in_shape(self, series, dt):
        self.assertLess(self.longest(series, dt), self.BRIEF)
        self.assertLess(self.longest(series, dt, -1.0), self.CROUCH)

    def test_no_action_holds_her_drawn_out(self):
        from characters.reference.motions.gestures import face_gesture, head_gesture
        from characters.reference.motions.moves import PROFILE_MOVES, peek, pounce, sidestep
        from characters.reference.motions import FACE_GESTURES, HEAD_GESTURES, MOVES
        from characters.reference.stage import Switch
        actions = {name: build("right", "left") for name, build in MOVES.items()}
        actions.update({name: make("right", "left") for name, (make, _) in PROFILE_MOVES.items()})
        actions.update({name: head_gesture(name) for name in HEAD_GESTURES})
        actions.update({name: face_gesture(name) for name in FACE_GESTURES})
        actions.update({"step": sidestep("right", "left", 0.5), "peek": peek("right", "left", 1.0),
                        "pounce": pounce("right", "left", 1.0)})
        dt = 1 / 240
        for name, m in actions.items():
            with self.subTest(name):
                self.assert_in_shape([m.sample(n * dt).get("Squash", 0.0) for n in range(int(m.duration / dt) + 1)],
                                     dt)
        hop = Switch().hop
        self.assert_in_shape([hop(n * dt) for n in range(int(0.52 / dt) + 1)], dt)

    def test_she_is_never_held_drawn_out_in_any_band(self):
        profile = {"signature": ["stretch", "yawn", "tiptoe", "curtsy", "hop", "pounce"], "bounce": 1.4}
        d = Director(body_params(), arm_out="right", arm_book="left", orb=-1.0, profile=profile)
        played, squash = set(), []
        dt = 1 / 60
        for n in range(int(240 / dt)):
            d.set_level([0, 0, 2, 6, 10, 13, 3, 0][(n * 8) // int(240 / dt)])
            squash.append(d.update(dt)["Squash"])
            played.add(d.last_gesture)
        self.assertTrue({"stretch", "yawn", "tiptoe", "curtsy"} <= played, played)
        self.assert_in_shape(squash, dt)


class AppendageTests(unittest.TestCase):
    def scene(self):
        from characters.rig.layers import Layer
        h = w = 200
        ref = np.ones((h, w, 3), np.float32)

        def layer(name, x0, y0, x1, y1, shade):
            rgba = np.zeros((h, w, 4), np.float32)
            rgba[y0:y1, x0:x1] = (shade, shade, shade, 1.0)
            ref[y0:y1, x0:x1] = shade
            return Layer(name, name, rgba)

        parts = [layer("topwear", 80, 60, 120, 160, 0.5), layer("face", 85, 20, 115, 55, 0.8)]
        ref[120:135, 120:175] = 0.2
        return parts, ref

    def test_art_no_layer_covers_becomes_the_tail(self):
        from characters.rig.appendages import recover_missing
        parts, ref = self.scene()
        self.assertEqual(recover_missing(parts, ref, {"tail": True}), ["tail"])
        tail = next(p for p in parts if p.tag == "tail")
        self.assertGreater(tail.alpha[127, 150], 0.99)
        self.assertEqual(tail.alpha[127, 190], 0.0)
        self.assertLess(parts.index(tail), [p.tag for p in parts].index("topwear"))
        parts, ref = self.scene()
        self.assertEqual(recover_missing(parts, ref, {}), ["extra"])
        parts, ref = self.scene()
        figure = np.ones(ref.shape[:2], np.float32)
        figure[120:135, 120:175] = 0.05
        self.assertEqual(recover_missing(parts, ref, {"tail": True}, figure=figure), [])
        parts, ref = self.scene()
        ref[120:135, 120:175] = (0.9, 0.3, 0.6)
        self.assertEqual(recover_missing(parts, ref, {"tail": True}, figure=figure), ["tail"])

    def test_shoes_the_split_left_out_become_footwear(self):
        from characters.rig.appendages import recover_missing
        from characters.rig.layers import Layer
        ref = np.ones((200, 200, 3), np.float32)
        ref[20:50, 85:115] = 0.8
        ref[60:170, 90:110] = 0.6
        ref[170:190, 85:115] = 0.2
        def layer(name, y0, y1, x0, x1):
            rgba = np.zeros((200, 200, 4), np.float32)
            rgba[y0:y1, x0:x1] = (0.5, 0.5, 0.5, 1.0)
            return Layer(name, name, rgba)
        parts = [layer("legwear", 60, 170, 90, 110), layer("face", 20, 50, 85, 115)]
        self.assertEqual(recover_missing(parts, ref, {"tail": True}), ["footwear"])

    def test_a_calf_above_a_kicked_up_slipper_joins_the_leg(self):
        from characters.rig.appendages import recover_missing
        from characters.rig.layers import Layer
        ref = np.ones((300, 300, 3), np.float32)
        ref[20:50, 135:165] = 0.8
        ref[120:150, 150:190] = 0.6
        ref[150:200, 140:200] = 0.3
        ref[60:105, 30:260] = 0.5
        def layer(name, *boxes):
            rgba = np.zeros((300, 300, 4), np.float32)
            for y0, y1, x0, x1 in boxes:
                rgba[y0:y1, x0:x1] = (0.5, 0.5, 0.5, 1.0)
            return Layer(name, name, rgba)
        parts = [layer("footwear", (150, 200, 140, 200)), layer("face", (20, 50, 135, 165))]
        self.assertEqual(sorted(recover_missing(parts, ref, {})), ["extra", "legwear"])

    def test_the_tail_chain_runs_from_the_body_to_the_tip(self):
        from characters.rig.appendages import recover_missing, tail_landmarks, tail_weights
        parts, ref = self.scene()
        recover_missing(parts, ref, {"tail": True})
        tail = next(p for p in parts if p.tag == "tail")
        lm = tail_landmarks(tail, parts, (100, 110))
        self.assertLess(abs(lm["root"][0] - 120), 4)
        xs = [p[0] for p in lm["pivots"]]
        self.assertEqual(xs, sorted(xs))
        self.assertGreater(lm["length"], 45)
        vertices = np.array([lm["root"], [140, 127], [160, 127], [176, 127]], np.float64)
        weights = dict(tail_weights(vertices, lm))
        np.testing.assert_allclose(sum(weights.values()), 1.0, atol=1e-9)
        self.assertGreater(weights["lower"][0], 0.99)
        self.assertGreater(weights["tail3"][-1], 0.9)

    def test_wings_turn_about_their_roots_beside_the_spine(self):
        from characters.rig.appendages import wing_landmarks, wing_weights
        from characters.rig.layers import Layer
        rgba = np.zeros((200, 200, 4), np.float32)
        rgba[60:120, 30:95, 3] = 1
        rgba[60:120, 105:170, 3] = 1
        lm = wing_landmarks([Layer("wings", "wings", rgba)], (100, 80))
        self.assertEqual(set(lm), {"left", "right"})
        self.assertLess(lm["left"]["root"][0], 100)
        self.assertGreater(lm["right"]["root"][0], 100)
        roots = [lm["left"]["root"], lm["right"]["root"]]
        weights = dict(wing_weights(np.array([[40.0, 90.0], [160.0, 90.0]] + roots), lm, (100, 80)))
        np.testing.assert_allclose(sum(weights.values()), 1.0, atol=1e-9)
        self.assertGreater(weights["wing.left"][0], 0.9)
        self.assertGreater(weights["wing.right"][1], 0.9)
        self.assertGreater(min(weights["chest"][2:]), 0.99)

    def test_animal_ears_sit_above_the_face(self):
        from characters.rig.appendages import animal_ears
        from characters.rig.layers import Layer
        top = np.zeros((200, 200, 4), np.float32)
        top[5:30, 70:85, 3] = 1
        side = np.zeros((200, 200, 4), np.float32)
        side[40:60, 60:70, 3] = 1
        ears = animal_ears([Layer("ears.left", "ears", top, "left"), Layer("ears.right", "ears", side, "right")],
                           (75, 25, 125, 75))
        self.assertEqual(set(ears), {"left"})
        self.assertEqual(ears["left"]["base"][1], 29)


    def test_a_gap_layers_surround_goes_to_the_lowest_layer_around_it(self):
        from characters.rig.appendages import fill_holes
        from characters.rig.layers import Layer
        ref = np.ones((400, 400, 3), np.float32)
        ref[10:70, 10:70] = (0.1, 0.4, 0.45)
        robe = np.zeros((400, 400, 4), np.float32)
        robe[10:70, 10:66] = 1
        robe[28:44, 28:44] = 0
        straps = np.zeros((400, 400, 4), np.float32)
        straps[24:48, 24:48] = 1
        straps[28:44, 28:44] = 0
        parts = [Layer("topwear", "topwear", robe), Layer("objects", "objects", straps)]
        self.assertGreater(fill_holes(parts, ref), 0)
        self.assertEqual(parts[0].alpha[36, 36], 1.0)
        np.testing.assert_allclose(parts[0].rgba[36, 36, :3], (0.1, 0.4, 0.45))
        self.assertEqual(parts[1].alpha[36, 36], 0.0)
        self.assertEqual(parts[0].alpha[40, 68], 0.0)

    def test_a_piece_cut_off_a_layer_goes_back_to_it_soft_where_it_meets_the_paper(self):
        from characters.rig.appendages import attach_fragments
        from characters.rig.layers import Layer
        ref = np.ones((600, 600, 3), np.float32)
        ref[40:60, 30:70] = 0.2
        ref[30:40, 42:58] = 0.85
        ref[200:210, 200:216] = 0.2
        crown = np.zeros((600, 600, 4), np.float32)
        crown[40:60, 30:70] = 1
        hair = np.zeros((600, 600, 4), np.float32)
        hair[60:100, 20:80] = 1
        ref[60:100, 20:80] = 0.4
        parts = [Layer("back hair", "back hair", hair), Layer("headwear", "headwear", crown)]
        self.assertEqual(attach_fragments(parts, ref), 1)
        self.assertEqual(parts[1].alpha[38, 50], 1.0)
        self.assertAlmostEqual(float(parts[1].alpha[30, 50]), 0.5, places=4)
        self.assertEqual(parts[0].alpha[35, 50], 0.0)
        self.assertEqual(parts[1].alpha[205, 208], 0.0)

class WaistTests(unittest.TestCase):
    def layers(self, top, bottom):
        from characters.rig.layers import Layer

        def layer(name, y0, y1, x0=40, x1=60):
            rgba = np.zeros((200, 100, 4), np.float32)
            rgba[y0:y1, x0:x1, 3] = 1
            return Layer(name, name, rgba)
        return [layer("face", 10, 30), layer("topwear", *top), layer("bottomwear", *bottom)]

    def test_a_skirt_layer_from_the_shoulders_does_not_lift_the_waist(self):
        from characters.rig.skeleton import landmarks
        torso = {"shoulder": 40.0, "hip": 100.0}
        tidy = landmarks(self.layers((35, 82), (78, 160)), torso)
        self.assertAlmostEqual(tidy["waist"][1], 79.5, delta=1)
        caped = landmarks(self.layers((35, 90), (40, 160)), torso)
        self.assertAlmostEqual(caped["waist"][1], 40 + 0.65 * 60, delta=0.5)
        self.assertAlmostEqual(landmarks(self.layers((35, 90), (40, 160)))["waist"][1], 64.5, delta=1)

    def test_skirt_weights_follow_the_chest_above_the_waist_and_swing_at_the_hem(self):
        from characters.rig.skeleton import skirt_weights
        lm = {"waist": (50, 100), "hem": 200, "skirtMid": 145}
        y = np.array([40.0, 100.0, 150.0, 200.0])
        weights = dict(skirt_weights(y, lm, 50.0))
        np.testing.assert_allclose(sum(weights.values()), 1.0, atol=1e-9)
        self.assertGreater(weights["chest"][0], 0.99)
        self.assertGreater(weights["lower"][1], 0.5)
        self.assertGreater(weights["skirtHem"][3], 0.99)


    def test_no_foot_bone_when_one_foot_is_kicked_up(self):
        from characters.rig.layers import Layer
        from characters.rig.skeleton import _one_foot_raised

        def feet(*boxes):
            rgba = np.zeros((300, 200, 4), np.float32)
            for y0, y1, x0, x1 in boxes:
                rgba[y0:y1, x0:x1, 3] = 1
            return [Layer("footwear", "footwear", rgba)]
        self.assertFalse(_one_foot_raised(feet((260, 290, 40, 80), (262, 290, 120, 160)), 200))
        self.assertTrue(_one_foot_raised(feet((260, 290, 40, 80), (180, 210, 120, 160)), 200))
        self.assertFalse(_one_foot_raised(feet((260, 290, 40, 160)), 200))

    def test_a_seated_character_keeps_skirt_and_legs_with_her_hips(self):
        from characters.rig.layers import Layer
        from characters.rig.skeleton import weights_for
        lm = {"face": (40, 0, 60, 20), "chin": 20, "waist": (50, 100), "hem": 200, "skirtMid": 145, "rigidLower": True}
        y = np.array([[50.0, 60.0], [50.0, 150.0], [50.0, 220.0]])
        flat = np.zeros((1, 1, 4), np.float32)
        legs = dict(weights_for(Layer("legwear", "legwear", flat), y, lm))
        self.assertEqual(set(legs), {"lower"})
        skirt = dict(weights_for(Layer("bottomwear", "bottomwear", flat), y, lm))
        self.assertEqual(set(skirt), {"chest", "lower"})
        self.assertGreater(skirt["lower"][2], 0.99)

    def test_body_drawn_into_an_arm_layer_follows_the_body(self):
        from characters.rig.layers import Layer
        from characters.rig.skeleton import weights_for
        left = {"shoulder": (60, 40), "elbow": (20, 60), "wrist": (0, 80), "tip": (-12, 92), "reach": 80.0}
        right = {"shoulder": (140, 40), "elbow": (180, 60), "wrist": (200, 80), "tip": (212, 92), "reach": 80.0}
        lm = {"face": (80, 0, 120, 30), "chin": 30, "waist": (100, 120), "arms": {"left": left, "right": right},
              "bodyInArms": True}
        v = np.array([[30.0, 55.0], [100.0, 90.0], [100.0, 200.0]])
        w = dict(weights_for(Layer("handwear.left", "handwear", np.zeros((1, 1, 4), np.float32), "left"), v, lm))
        np.testing.assert_allclose(sum(w.values()), 1.0)
        self.assertLess(w["chest"][0] + w["lower"][0], 0.01)
        self.assertGreater(w["chest"][1] + w["lower"][1], 0.99)
        self.assertGreater(w["lower"][2], 0.99)

class PaperTests(unittest.TestCase):
    def test_enclosed_white_is_paper_unless_a_layer_or_a_sure_matte_claims_it(self):
        from characters.rig.matte import paper
        ref = np.ones((60, 60, 3), np.float32)
        ref[10:50, 10:50] = 0.3
        ref[20:40, 20:40] = 1.0
        hole = (slice(20, 40), slice(20, 40))
        unsure = np.ones((60, 60), np.float32)
        unsure[hole] = 0.93
        uncovered = np.ones((60, 60), bool)
        uncovered[hole] = False
        self.assertFalse(paper(ref, unsure)[30, 30])
        self.assertTrue(paper(ref, unsure, covered=uncovered)[30, 30])
        self.assertFalse(paper(ref, unsure, covered=np.ones((60, 60), bool))[30, 30])
        sure = unsure.copy()
        sure[hole] = 0.995
        self.assertFalse(paper(ref, sure, covered=uncovered)[30, 30])
        loose = unsure.copy()
        loose[hole] = 0.6
        self.assertTrue(paper(ref, loose, covered=np.ones((60, 60), bool))[30, 30])
        self.assertTrue(paper(ref, np.zeros((60, 60), np.float32))[0, 0])
        self.assertFalse(paper(ref, unsure, covered=uncovered)[15, 15])


class TailCopyTests(unittest.TestCase):
    def layer(self, name, color, *boxes):
        from characters.rig.layers import Layer
        rgba = np.zeros((200, 200, 4), np.float32)
        for y0, y1, x0, x1 in boxes:
            rgba[y0:y1, x0:x1] = (*color, 1.0)
        return Layer(name, name, rgba)

    def test_a_tail_hanging_in_front_of_the_skirt_is_drawn_over_it_and_the_copy_goes(self):
        from characters.rig.layers import tail_alone
        ref = np.ones((200, 200, 3), np.float32)
        ref[40:160, 40:180] = 0.2
        ref[100:190, 100:124] = 0.9
        skirt = self.layer("bottomwear", (0.2, 0.2, 0.2), (40, 160, 40, 180), (100, 190, 100, 124))
        skirt.rgba[100:190, 100:124, :3] = 0.9
        tail = self.layer("tail", (0.9, 0.9, 0.9), (100, 190, 100, 124))
        parts, repainted = tail_alone([tail, skirt], ref)
        self.assertEqual([q.name for q in parts], ["bottomwear", "tail"])
        self.assertEqual(repainted, ["bottomwear"])
        self.assertEqual(skirt.alpha[180, 110], 0.0)
        self.assertEqual(skirt.alpha[130, 110], 1.0)
        self.assertLess(skirt.rgba[130, 110, 0], 0.5)

    def test_a_tail_drawn_into_the_front_hair_goes_back_to_the_tail_but_the_strand_stays(self):
        from characters.rig.layers import tail_from_hair
        ref = np.ones((200, 200, 3), np.float32)
        ref[10:40, 80:120] = 0.1
        ref[40:110, 95:105] = 0.1
        ref[110:180, 60:160] = 0.4
        ref[180:195, 90:130] = 0.95
        hair = self.layer("front hair", (0.1, 0.1, 0.1), (10, 110, 95, 105), (110, 180, 60, 160))
        hair.rgba[110:180, 60:160, :3] = 0.4
        tail = self.layer("tail", (0.95, 0.95, 0.95), (180, 195, 90, 130))
        moved = tail_from_hair([tail, hair], ref, (80, 10, 120, 40))
        self.assertGreater(moved, 5000)
        self.assertEqual(tail.alpha[150, 110], 1.0)
        self.assertEqual(hair.alpha[150, 110], 0.0)
        self.assertEqual(hair.alpha[80, 100], 1.0)
        self.assertEqual(tail.alpha[80, 100], 0.0)

    def test_the_top_of_a_tail_drawn_into_the_back_hair_goes_to_the_tail(self):
        from characters.rig.layers import seal_tail, tail_from_back_hair
        hair = self.layer("back hair", (0.2, 0.2, 0.3), (10, 150, 40, 160))
        tail = self.layer("tail", (0.3, 0.3, 0.3), (120, 190, 100, 150))
        tail.rgba[150:152, 110:140, 3] = 0.4
        hair.rgba[90:120, 100:150, :3] = 0.3
        mask = np.zeros((200, 200), bool)
        mask[90:190, 100:150] = True
        parts = [hair, tail]
        self.assertEqual(tail_from_back_hair(parts, mask), 30 * 50)
        self.assertEqual(tail.alpha[100, 125], 1.0)
        self.assertEqual(hair.alpha[100, 125], 1.0)
        self.assertLess(abs(hair.rgba[100, 125, 2] - 0.3), 0.06)
        seal_tail(parts, np.full((200, 200, 3), 0.3, np.float32))
        self.assertEqual(tail.alpha[151, 125], 1.0)
        self.assertEqual(tail_from_back_hair(parts, None), 0)

    def test_what_the_tail_uncovers_by_its_root_shows_the_coat_under_it(self):
        from characters.rig.layers import back_tail_root
        coat = self.layer("bottomwear", (0.1, 0.1, 0.1), (20, 190, 20, 90))
        tail = self.layer("tail", (0.5, 0.5, 0.5), (60, 190, 80, 140))
        self.assertGreater(back_tail_root([coat, tail], (85, 62), 60), 0)
        self.assertEqual(coat.alpha[80, 100], 1.0)
        self.assertAlmostEqual(float(coat.rgba[80, 100, 0]), 0.1, places=5)
        self.assertEqual(coat.alpha[180, 130], 0.0)

    def test_a_notch_between_the_pieces_of_a_tail_is_filled_from_the_art(self):
        from characters.rig.layers import seal_tail
        ref = np.full((200, 200, 3), 0.6, np.float32)
        tail = self.layer("tail", (0.3, 0.3, 0.3), (40, 160, 60, 140))
        tail.rgba[90:110, 120:140, 3] = 0.0
        seal_tail([tail], ref)
        self.assertEqual(tail.alpha[100, 135], 1.0)
        self.assertAlmostEqual(float(tail.rgba[100, 135, 0]), 0.6, places=5)
        self.assertAlmostEqual(float(tail.rgba[60, 100, 0]), 0.3, places=5)
        self.assertEqual(tail.alpha[100, 150], 0.0)

    def test_a_copy_of_the_tail_in_the_back_hair_goes_and_a_tail_behind_the_skirt_stays(self):
        from characters.rig.layers import tail_alone
        ref = np.ones((200, 200, 3), np.float32)
        ref[20:160, 40:180] = 0.3
        ref[60:190, 100:126] = 0.8
        hair = self.layer("back hair", (0.3, 0.3, 0.3), (20, 160, 40, 180))
        hair.rgba[60:160, 100:126, :3] = 0.8
        tail = self.layer("tail", (0.8, 0.8, 0.8), (60, 190, 100, 126))
        parts, repainted = tail_alone([hair, tail], ref)
        self.assertEqual([q.name for q in parts], ["back hair", "tail"])
        self.assertEqual(repainted, ["back hair"])
        shows_copy = hair.alpha[90:160, 100:126] * (hair.rgba[90:160, 100:126, 0] > 0.6)
        self.assertEqual(float(shows_copy.max()), 0.0)
        skirt = self.layer("bottomwear", (0.2, 0.2, 0.2), (100, 190, 60, 180))
        ref2 = ref.copy()
        ref2[100:190, 60:180] = 0.2
        tail2 = self.layer("tail", (0.8, 0.8, 0.8), (60, 180, 100, 160))
        parts, repainted = tail_alone([tail2, skirt], ref2)
        self.assertEqual([q.name for q in parts], ["tail", "bottomwear"])
        self.assertEqual(repainted, [])


class HiddenLegTests(unittest.TestCase):
    def test_legs_under_the_skirt_take_the_colors_of_the_legs_that_show(self):
        from characters.rig.layers import Layer, fill_hidden_legs
        legs = np.zeros((200, 100, 4), np.float32)
        legs[40:190, 30:70] = (0.4, 0.4, 0.45, 0.5)
        legs[120:190, 35:65] = (0.95, 0.9, 0.9, 1.0)
        skirt = np.zeros((200, 100, 4), np.float32)
        skirt[20:120, 10:90] = (0.2, 0.2, 0.6, 1.0)
        parts = [Layer("legwear", "legwear", legs), Layer("bottomwear", "bottomwear", skirt)]
        self.assertGreater(fill_hidden_legs(parts), 0)
        np.testing.assert_allclose(parts[0].rgba[80, 50], (0.95, 0.9, 0.9, 1.0))
        np.testing.assert_allclose(parts[0].rgba[150, 50], (0.95, 0.9, 0.9, 1.0))
        self.assertEqual(parts[0].alpha[10, 50], 0.0)


class EyePairTests(unittest.TestCase):
    def test_the_open_eye_of_a_wink_takes_its_side(self):
        from characters.rig.layers import Layer, split_pairs
        rgba = np.zeros((100, 200, 4), np.float32)
        rgba[40:50, 40:60, 3] = 1
        arms = np.zeros((100, 200, 4), np.float32)
        arms[60:90, 20:40, 3] = 1
        parts = split_pairs([Layer("irides", "irides", rgba), Layer("handwear", "handwear", arms)], 100)
        self.assertEqual([(p.name, p.side) for p in parts], [("irides.left", "left"), ("handwear", None)])


class ObjectPieceTests(unittest.TestCase):
    def test_a_fan_in_one_hand_and_a_purse_at_the_waist_part_ways(self):
        from characters.rig.layers import Layer, split_objects
        from characters.rig.skeleton import holding_side
        rgba = np.zeros((200, 200, 4), np.float32)
        rgba[20:60, 20:60, 3] = 1
        rgba[140:180, 140:180, 3] = 1
        rgba[62:64, 62:64, 3] = 1
        pieces = split_objects([Layer("objects", "objects", rgba)])
        self.assertEqual([p.name for p in pieces], ["objects", "objects2"])
        self.assertEqual(pieces[0].alpha[63, 63], 1.0)
        self.assertEqual(pieces[1].alpha[63, 63], 0.0)
        arms = {"left": {"wrist": (10, 70), "tip": (30, 60)}, "right": {"wrist": (190, 20), "tip": (180, 10)}}
        lm = {"arms": arms, "hands": {}}
        self.assertEqual(holding_side(pieces[0], lm, 20), "left")
        self.assertIsNone(holding_side(pieces[1], lm, 20))
        lone = {"arms": {"single": {"shoulder": (100, 100), "elbow": (150, 130), "hand": (150, 150), "wrist": None}},
                "hands": {}}
        self.assertEqual(holding_side(pieces[1], lone, 20), "single")


class PropHintTests(unittest.TestCase):
    def test_a_prop_follows_the_hand_unless_it_names_a_body_bone(self):
        from characters.rig.build import prop_hint
        hints = {"props": {"idle": [[0.1, 0.2]], "boost": {"points": [[0.3, 0.4]], "follow": "chest"}}}
        self.assertEqual(prop_hint(hints, "idle"), ([[0.1, 0.2]], None))
        self.assertEqual(prop_hint(hints, "boost"), ([[0.3, 0.4]], "chest"))
        self.assertEqual(prop_hint(hints, "cruise"), (None, None))
        self.assertEqual(prop_hint(hints, None), (None, None))
        with self.assertRaises(ValueError):
            prop_hint({"props": {"idle": {"points": [], "follow": "tail1"}}}, "idle")


class LayerTests(unittest.TestCase):
    def test_a_layer_the_face_covers_entirely_is_dropped(self):
        from characters.rig.layers import Layer, drop_empty, reproject
        face = np.zeros((40, 40, 4), np.float32)
        face[5:35, 5:35] = 1
        nose = np.zeros((40, 40, 4), np.float32)
        nose[18:20, 18:20, 3] = 0.2
        parts = [Layer("face", "face", face), Layer("nose", "nose", nose)]
        reproject(parts, np.full((40, 40, 3), 0.5, np.float32))
        self.assertEqual([p.name for p in drop_empty(parts)], ["face"])

    def test_a_held_prop_leaves_the_layers_that_draw_it_but_not_what_hides_behind_it(self):
        from characters.rig.layers import Layer, take_prop

        def layer(name, *boxes):
            rgba = np.zeros((40, 40, 4), np.float32)
            for y0, y1, x0, x1 in boxes:
                rgba[y0:y1, x0:x1] = 1
            return Layer(name, name, rgba)
        body = layer("topwear", (0, 40, 15, 40), (15, 40, 0, 15))
        parts = [layer("back hair", (5, 15, 5, 15)), body, layer("face", (15, 25, 5, 15), (30, 35, 30, 35))]
        mask = np.zeros((40, 40), bool)
        mask[5:25, 5:15] = True
        mask[2:5, 5:15] = True
        parts = take_prop(parts, mask, np.full((40, 40, 3), 0.5, np.float32), grow=0, reach=2)
        self.assertEqual([p.name for p in parts], ["back hair", "topwear", "prop", "face"])
        self.assertEqual(parts[2].tag, "objects")
        self.assertTrue((parts[2].alpha[mask] == 1).all())
        self.assertEqual(parts[0].alpha[10, 10], 0.0)
        self.assertEqual(parts[3].alpha[20, 10], 0.0)
        self.assertEqual(parts[1].alpha[20, 10], 1.0)
        self.assertEqual(parts[3].alpha[32, 32], 1.0)
        self.assertEqual(parts[2].alpha[3, 10], 1.0)

    def test_a_hat_drawn_into_the_top_goes_to_the_head_and_a_tossed_one_is_set_back_on_it(self):
        from characters.rig.layers import Layer, lift_hats

        def layer(name, tag, *boxes):
            rgba = np.zeros((300, 200, 4), np.float32)
            for y0, y1, x0, x1 in boxes:
                rgba[y0:y1, x0:x1] = 1
            return Layer(name, tag, rgba)
        face = (80, 100, 120, 140)
        for gap, seated in ((0, False), (30, True)):
            hair = layer("front hair", "front hair", (90, 110, 70, 130))
            top = layer("topwear", "topwear", (150, 250, 60, 140), (50 - gap, 90 - gap, 70, 130))
            parts, lifted = lift_hats([layer("face", "face", (100, 140, 80, 120)), top, hair], face)
            self.assertEqual(lifted, 1)
            names = [q.name for q in parts]
            hat = parts[names.index("hat")]
            self.assertEqual(hat.tag, "headwear")
            self.assertEqual(top.alpha[60 - gap, 100], 0.0)
            self.assertEqual(top.alpha[200, 100], 1.0)
            self.assertEqual(names.index("hat"), 3 if seated else 2)
            ys = np.nonzero(hat.alpha[:, 100] > 0.5)[0]
            self.assertGreaterEqual(ys.max(), 90 + (10 if seated else -1))

    def test_a_robe_front_drawn_into_the_arm_layer_goes_to_the_body_up_to_the_ink_line(self):
        from characters.rig.layers import Layer, split_body_from_arms
        ref = np.ones((200, 200, 3), np.float32)
        ref[60:190, 80:100] = 0.3
        ref[60:190, 100:102] = 0.0
        ref[60:190, 102:150] = 0.6
        rgba = np.zeros((200, 200, 4), np.float32)
        rgba[60:190, 80:150, 3] = 1
        arm = Layer("handwear.right", "handwear", rgba, "right")
        arms = {"left": {"shoulder": (40, 60), "elbow": (35, 120), "wrist": (32, 180)},
                "right": {"shoulder": (140, 60), "elbow": (135, 120), "wrist": (132, 180)}}
        parts, moved = split_body_from_arms([arm], arms, ref, (75, 10, 105, 40))
        self.assertEqual([q.name for q in parts], ["handwear.right", "handwear.right body"])
        self.assertGreater(moved, 1000)
        self.assertEqual(parts[1].tag, "topwear")
        self.assertEqual(parts[1].alpha[120, 90], 1.0)
        self.assertEqual(parts[0].alpha[120, 90], 0.0)
        self.assertEqual(parts[0].alpha[120, 125], 1.0)
        self.assertEqual(parts[1].alpha[120, 125], 0.0)

    def test_hair_painted_behind_a_held_doll_goes_with_the_doll(self):
        from characters.rig.layers import Layer, take_prop
        hair = np.zeros((80, 80, 4), np.float32)
        hair[10:30, 10:30] = 1
        hair[50:80, 0:80] = 1
        hair[28:34, 12:14] = 1
        face = np.zeros((80, 80, 4), np.float32)
        face[12:28, 12:28] = 1
        mask = np.zeros((80, 80), bool)
        mask[12:28, 12:28] = True
        parts = take_prop([Layer("back hair", "back hair", hair), Layer("face", "face", face)], mask,
                          np.full((80, 80, 3), 0.5, np.float32), grow=0, reach=6)
        hair_left, prop, face_left = parts
        self.assertEqual(prop.name, "prop")
        self.assertEqual(hair_left.alpha[11, 11], 0.0)
        self.assertEqual(hair_left.alpha[33, 13], 0.0)
        self.assertEqual(hair_left.alpha[60, 40], 1.0)
        self.assertEqual(prop.alpha[11, 11], 1.0)
        self.assertEqual(prop.alpha[33, 13], 1.0)
        self.assertEqual(face_left.alpha[20, 20], 0.0)

class ParticleTests(unittest.TestCase):
    def test_every_kind_emits_and_draws(self):
        from characters.reference.effects import PARTICLES, Particles
        for kind in PARTICLES:
            p = Particles(kind, (0, 0, 600, 900), accent="#4D6BFE")
            for _ in range(240):
                p.update(1 / 60, 0.0 if kind == "zzz" else 1.0)
            self.assertTrue(p.items, kind)
            rows = len(p.dots(True)) + len(p.dots(False))
            glyphs = len(p.glyphs(True)) + len(p.glyphs(False))
            self.assertTrue(glyphs == len(p.items) if PARTICLES[kind]["look"] == "glyph" else rows > 0, kind)

    def test_sleepy_kinds_drift_only_at_rest(self):
        from characters.reference.effects import Particles
        p = Particles("zzz", (0, 0, 600, 900))
        for _ in range(300):
            p.update(1 / 60, 1.0)
        self.assertFalse(p.items)

    def test_unknown_kinds_are_petals(self):
        from characters.reference.effects import Particles
        self.assertEqual(Particles("lasers", (0, 0, 10, 10)).kind, "petals")
