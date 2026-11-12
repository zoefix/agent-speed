"""Behavior: moods from the agents' phases, mood poses, face patches, talking and looking at the user
(spec/CHARACTERS.md, Behavior)."""
import json
import math
import unittest
from pathlib import Path

from characters.reference.motions import Director
from characters.reference.motions.behavior import KEEP, Behavior, Inputs, Talk
from characters.reference.rng import Rng

DT = 1 / 60
PARAMS = json.loads((Path(__file__).parent / "director_params.json").read_text())
ALL_STATES = ("think", "wait", "cheer", "sleepy", "focus", "greet")


def variants(expressions=("smile", "laugh", "surprised", "thinking", "sleepy", "yawn", "focused", "worried", "excited",
                          "wink"), mouths=("a", "i", "u", "o"), gazes=("left", "right", "up", "down", "closed"),
             arms=("wave", "peace", "chin")):
    out = []
    for kind, names, region in (("expr", expressions, "face"), ("mouth", mouths, "mouth"), ("gaze", gazes, "eyes")):
        out += [{"id": f"{kind}-{n}", "kind": "patch", "param": f"Var.{kind}-{n}", "region": region} for n in names]
    for gid in arms:
        out.append({"id": gid, "kind": "arm", "param": f"Var.{gid}", "side": "right", "front": True,
                    "arm": f"Arm.right@{gid}", "forearm": f"Forearm.right@{gid}",
                    "lead": {"Lift.right": 0.3, "Bend.right": -0.2},
                    "approach": {f"Arm.right@{gid}": -0.2, f"Forearm.right@{gid}": 0.1}})
    return out


def director(states=ALL_STATES, seed=3, **kw):
    vs = variants(**kw)
    params = {p["id"]: p for p in PARAMS}
    for v in vs:
        params[v["param"]] = {"id": v["param"], "min": 0, "max": 1, "default": 0}
        if v["kind"] == "arm":
            for pid in (v["arm"], v["forearm"]):
                params[pid] = {"id": pid, "min": -1, "max": 1, "default": 0}
    return Director(params, vs, seed=seed, arm_out="right", arm_book="left", orb=1.0, celebrate=False, states=states)


def run(d, seconds, level=0, frames=None, **inputs):
    """Steps the director with fixed inputs; returns the last values (and appends every frame's to `frames`)."""
    values = None
    for _ in range(int(round(seconds * 60))):
        d.set_level(level)
        values = d.update(DT, Inputs(**inputs))
        if frames is not None:
            frames.append((d.behavior.mood, d.pose_wanted(), d.expr, d.gaze_patch, dict(values)))
    return values


class MoodTests(unittest.TestCase):
    def test_the_first_token_wait_turns_to_thinking_and_output_to_work(self):
        d = director()
        run(d, 2.0, phase="generating", wait=0.0)
        self.assertEqual(d.behavior.mood, "work")
        run(d, 2.0, phase="generating", wait=3.5)
        self.assertEqual(d.behavior.mood, "think")
        self.assertIsNone(d.pose_wanted())
        run(d, 0.5, phase="generating", wait=10.0)
        self.assertEqual(d.pose_wanted(), "think")
        run(d, 1.3, level=3, phase="generating")
        self.assertEqual(d.behavior.mood, "work")
        self.assertIsNone(d.pose_wanted())

    def test_a_short_tool_call_stays_work_and_a_long_one_is_focus(self):
        d = director()
        run(d, 2.0, level=3, phase="generating")
        run(d, 1.0, phase="tools")
        self.assertEqual(d.behavior.mood, "work")
        run(d, 2.5, phase="tools")
        self.assertEqual(d.behavior.mood, "focus")
        self.assertIsNone(d.pose_wanted())
        run(d, 9.0, phase="tools")
        self.assertEqual(d.pose_wanted(), "focus")

    def test_a_mood_holds_before_the_next_one(self):
        d = director()
        run(d, 2.0, phase="generating", wait=5.0)
        self.assertEqual(d.behavior.mood, "think")
        run(d, 0.5, level=2, phase="generating")
        self.assertEqual(d.behavior.mood, "work")
        run(d, 0.5, phase="generating", wait=5.0)
        self.assertEqual(d.behavior.mood, "work", "work started 0.5 s ago and holds 1.2 s")
        run(d, 0.8, phase="generating", wait=5.0)
        self.assertEqual(d.behavior.mood, "think")

    def test_she_rests_after_ten_minutes_or_two_at_night(self):
        d = director()
        run(d, 1.0, idle=300.0, hour=14)
        self.assertEqual(d.behavior.mood, "wait")
        run(d, 1.0, idle=601.0, hour=14)
        self.assertEqual((d.behavior.mood, d.pose_wanted()), ("rest", "sleepy"))
        d = director()
        run(d, 1.0, idle=130.0, hour=2)
        self.assertEqual(d.behavior.mood, "rest")

    def test_a_click_wakes_her_for_a_minute(self):
        d = director()
        run(d, 1.0, idle=900.0, hour=3)
        self.assertEqual(d.behavior.mood, "rest")
        run(d, DT, idle=900.0, hour=3, cursor=(0.0, 0.3), over=True, click=True)
        self.assertEqual(d.behavior.mood, "poke")
        self.assertEqual(d.pose_wanted(), KEEP)
        run(d, 2.0, idle=900.0, hour=3)
        self.assertEqual(d.behavior.mood, "wait")
        run(d, 60.0, idle=900.0, hour=3)
        self.assertEqual(d.behavior.mood, "rest")

    def test_she_greets_only_when_work_wakes_her(self):
        d = director()
        run(d, 1.0, idle=900.0, hour=3)
        run(d, 0.1, phase="generating", wait=0.0)
        self.assertEqual((d.behavior.mood, d.pose_wanted()), ("greet", "greet"))
        run(d, 2.4, phase="generating", wait=2.5)
        self.assertEqual(d.behavior.mood, "work")
        d = director()
        run(d, 1.0, idle=20.0)
        run(d, 0.1, phase="generating", wait=0.0)
        self.assertEqual(d.behavior.mood, "work")

    def test_a_new_peak_cheers_and_a_click_wins_over_it(self):
        d = director()
        run(d, 1.0, level=9, phase="generating")
        run(d, DT, level=9, phase="generating", events=("newPeak",))
        self.assertEqual((d.behavior.mood, d.pose_wanted()), ("cheer", "cheer"))
        run(d, 1.0, level=9, phase="generating")
        run(d, DT, level=9, phase="generating", cursor=(0.0, 0.3), over=True, click=True)
        self.assertEqual(d.behavior.mood, "poke")
        run(d, 1.7, level=9, phase="generating")
        self.assertEqual(d.behavior.mood, "cheer", "the poke ended, the cheer goes on")
        run(d, 0.6, level=9, phase="generating")
        self.assertEqual(d.behavior.mood, "work", "the cheer ended 3.2 s after the peak")

    def test_trouble_worries_her_and_keeps_the_pose(self):
        d = director()
        run(d, 1.0, phase="tools")
        run(d, DT, phase="tools", events=("trouble",))
        run(d, 0.3, phase="tools")
        self.assertEqual((d.behavior.mood, d.pose_wanted(), d.expr), ("trouble", KEEP, "worried"))
        run(d, 2.3, phase="tools")
        self.assertEqual(d.behavior.mood, "focus")

    def test_she_yawns_while_resting_in_a_pose_without_patches(self):
        d = director()
        d.set_level(0)
        d.set_sides("right", "left", pose_band="sleepy", variants=())
        played = set()
        for _ in range(60 * 4):
            d.set_level(0)
            d.update(DT, Inputs(idle=900.0, hour=3))
            played.add(d.last_gesture if d.gesture else None)
        self.assertIn("yawn", played)

    def test_a_mood_pose_the_pack_lacks_is_the_band_pose(self):
        d = director(states=("cheer",))
        run(d, 1.0, idle=900.0, hour=3)
        self.assertEqual(d.behavior.mood, "rest")
        self.assertIsNone(d.pose_wanted())

    def test_the_wait_pose_comes_after_twenty_seconds_and_gives_way_to_a_hover(self):
        d = director()
        run(d, 1.0, idle=10.0)
        self.assertIsNone(d.pose_wanted())
        run(d, 1.0, idle=25.0)
        self.assertEqual(d.pose_wanted(), "wait")
        run(d, 1.0, idle=26.0, cursor=(0.0, 0.3), over=True)
        self.assertIsNone(d.pose_wanted())
        run(d, 7.0, idle=33.0, cursor=(1.2, 0.0))
        self.assertIsNone(d.pose_wanted(), "attention lasts 8 s after the cursor leaves")
        run(d, 1.2, idle=34.0, cursor=(1.2, 0.0))
        self.assertEqual(d.pose_wanted(), "wait")


class FaceTests(unittest.TestCase):
    def test_an_expression_swaps_while_the_eyes_are_shut(self):
        d = director()
        frames = []
        run(d, 4.0, phase="tools", frames=frames)
        swaps = [k for k in range(1, len(frames)) if frames[k][2] != frames[k - 1][2]]
        self.assertTrue(swaps)
        for k in swaps:
            self.assertLessEqual(frames[k][4]["EyeOpen.left"], 0.02)

    def test_faces_swap_at_fifteen_frames_a_second(self):
        d = director()
        for _ in range(15 * 2):
            d.set_level(0)
            d.update(1 / 15, Inputs(phase="generating", wait=5.0))
        self.assertEqual(d.expr, "thinking")
        for _ in range(15 * 5):
            d.update(1 / 15, Inputs(phase="tools"))
        self.assertEqual(d.expr, "focused")

    def test_a_blink_over_a_patch_shows_gaze_closed_briefly(self):
        d = director()
        frames = []
        run(d, 12.0, level=3, phase="generating", frames=frames)
        shut = [f[4]["Var.gaze-closed"] for f in frames if f[2] == "smile"]
        self.assertGreater(max(shut), 0.99)
        runs, n = [], 0
        for v in shut:
            n = n + 1 if v > 0.5 else (runs.append(n) or 0) if n else 0
        self.assertTrue(runs and max(runs) <= 9, runs)

    def test_no_blink_over_closed_eyes(self):
        d = director()
        frames = []
        run(d, DT, idle=10.0, cursor=(0.0, 0.3), over=True, click=True)
        run(d, 1.4, idle=10.0, cursor=(0.0, 0.3), over=True, frames=frames)
        laughing = [f[4]["Var.gaze-closed"] for f in frames if f[2] == "laugh"]
        self.assertTrue(laughing)
        self.assertLess(max(laughing[3:]), 1e-9)

    def test_the_gaze_follows_the_cursor_only_without_an_expression(self):
        d = director()
        run(d, 8.0, idle=60.0)
        run(d, 1.5, idle=61.0, cursor=(0.9, 0.1))
        self.assertEqual((d.expr, d.gaze_patch), (None, "right"))
        run(d, 1.5, idle=62.0, cursor=(-0.2, -0.8))
        self.assertEqual(d.gaze_patch, "up")
        run(d, 1.5, idle=63.0, cursor=(0.0, 0.3), over=True)
        self.assertEqual((d.expr, d.gaze_patch), ("smile", None))

    def test_without_patches_the_drawn_face_stays(self):
        d = director(expressions=(), mouths=(), gazes=())
        values = run(d, 6.0, level=3, phase="generating")
        self.assertIsNone(d.expr)
        self.assertFalse(any(k.startswith("Var.expr") for k in values))


class TalkTests(unittest.TestCase):
    def test_she_talks_in_phrases_of_a_o_u_with_pauses_and_silences(self):
        talk = Talk(Rng(5))
        shapes, opens, open_frames, stages = [], 0, 0, set()
        prev = None
        for _ in range(60 * 120):
            shape = talk.update(DT, True, 13, ["a", "i", "u", "o"])
            stages.add(talk.stage)
            if shape is not None and prev is None:
                shapes.append(shape)
                opens += 1
            open_frames += shape is not None
            prev = shape
        self.assertNotIn("i", shapes)
        share = {s: shapes.count(s) / len(shapes) for s in "aou"}
        self.assertGreater(share["a"], 0.45)
        self.assertGreater(share["o"], 0.12)
        self.assertGreater(share["u"], 0.05)
        self.assertLessEqual({"open", "gap", "pause", "silence"}, stages)
        self.assertLess(opens / 120, 7.0 * 0.75, "the pauses and silences thin out the syllables")
        self.assertLess(open_frames / (60 * 120), 0.5)

    def test_a_syllable_rate_stays_between_five_and_seven(self):
        for level in (1, 13):
            talk = Talk(Rng(level))
            for _ in range(60 * 30):
                talk.update(DT, True, level, ["a", "o", "u"])
                self.assertTrue(5.0 <= talk.rate <= 7.0, talk.rate)

    def test_a_group_waits_for_the_face_to_clear(self):
        talk = Talk(Rng(2))
        for _ in range(30):
            self.assertIsNone(talk.update(DT, True, 3, ["a", "o"], clear=False))
        self.assertTrue(talk.speaking)
        opened = [talk.update(DT, True, 3, ["a", "o"], clear=True) for _ in range(30)]
        self.assertTrue(any(opened))

    def test_she_talks_only_while_output_flows(self):
        d = director()
        run(d, 3.0, level=4, phase="generating")
        frames = []
        run(d, 8.0, level=4, phase="generating", frames=frames)
        opened = max(max(f[4][f"Var.mouth-{s}"] for s in "aiuo") for f in frames)
        self.assertGreater(opened, 0.99)
        self.assertEqual(max(f[4]["Var.mouth-i"] for f in frames), 0.0)
        frames = []
        run(d, 2.0, level=0, phase="generating", wait=1.0, frames=frames)
        values = frames[-1][4]
        self.assertEqual(max(values[f"Var.mouth-{s}"] for s in "aiuo"), 0.0)

    def test_a_silent_character_never_moves_her_lips(self):
        d = director()
        d.profile.talk = False
        frames = []
        run(d, 4.0, level=4, phase="generating", frames=frames)
        self.assertEqual(max(max(f[4][f"Var.mouth-{s}"] for s in "aiuo") for f in frames), 0.0)


    def test_mouths_switch_on_a_frame_and_never_mix_with_an_expression(self):
        d = director()
        frames = []
        run(d, 20.0, level=4, phase="generating", frames=frames)
        spoke = smiled = False
        for _, _, expr, gaze, values in frames:
            mouths = [values[f"Var.mouth-{s}"] for s in "aiuo"]
            self.assertTrue(all(m in (0.0, 1.0) for m in mouths), mouths)
            self.assertLessEqual(sum(mouths), 1.0)
            if any(mouths):
                spoke = True
                self.assertIsNone(expr)
                self.assertIsNone(gaze)
                self.assertFalse(any(values[f"Var.expr-{e}"] for e in ("smile", "excited", "focused")))
            smiled |= expr == "smile"
        self.assertTrue(spoke and smiled, "she smiles in the silences between groups of phrases")


class LookTests(unittest.TestCase):
    def test_the_head_turns_toward_the_cursor_and_back(self):
        d = director()
        run(d, 6.0, idle=60.0)
        right = run(d, 1.5, idle=61.0, cursor=(0.9, 0.0))["AngleX"]
        d2 = director()
        run(d2, 6.0, idle=60.0)
        left = run(d2, 1.5, idle=61.0, cursor=(-0.9, 0.0))["AngleX"]
        self.assertGreater(right - left, 0.8)
        self.assertGreater(d.behavior.look_w, 0.99)
        run(d, 1.0, idle=62.0)
        self.assertLess(d.behavior.look_w, 1e-9)

    def test_a_far_cursor_is_ignored(self):
        d = director()
        run(d, 2.0, idle=60.0, cursor=(2.5, 0.0))
        self.assertEqual(d.behavior.look_w, 0.0)

    def test_thinking_looks_up(self):
        d = director()
        values = run(d, 4.0, phase="generating", wait=5.0, cursor=(0.9, 0.0))
        self.assertEqual(d.behavior.look_mode, "up")
        self.assertLess(values["EyeBallY"], -0.3)


class HoverTests(unittest.TestCase):
    def test_hovering_smiles_then_gestures_and_leaving_waves(self):
        d = director()
        run(d, 10.0, idle=60.0)
        run(d, 1.2, idle=61.0, cursor=(0.0, 0.3), over=True)
        self.assertEqual(d.expr, "smile")
        played = set()
        for _ in range(60 * 6):
            d.update(DT, Inputs(idle=62.0, cursor=(0.0, 0.3), over=True))
            played.add(d.last_gesture if d.gesture else None)
        self.assertTrue({"wave", "peace"} & played, played)
        for _ in range(60 * 4):
            d.update(DT, Inputs(idle=63.0, cursor=(0.9, 0.0)))
            played.add(d.last_gesture if d.gesture else None)
        self.assertIn("wave", played)

    def test_without_arm_gestures_a_hover_winks(self):
        d = director(arms=())
        run(d, 10.0, idle=60.0)
        faces = set()
        for _ in range(60 * 6):
            d.update(DT, Inputs(idle=61.0, cursor=(0.0, 0.3), over=True))
            faces.add(d.expr)
        self.assertIn("wink", faces)


class CompatibilityTests(unittest.TestCase):
    def test_without_inputs_a_living_pack_moves_as_before(self):
        living = director()
        arms = [v for v in variants() if v["kind"] == "arm"]
        plain = Director(living.params, arms, seed=3, arm_out="right", arm_book="left", orb=1.0, celebrate=False)
        self.assertIsNone(plain.behavior)
        for n in range(60 * 30):
            for d in (living, plain):
                d.set_level((n // 300) % 14)
            a, b = living.update(DT), plain.update(DT)
            for pid, v in b.items():
                self.assertEqual(a[pid], v, pid)
            self.assertIsNone(living.pose_wanted())

    def test_a_pack_without_patches_or_states_has_no_behavior(self):
        d = Director({p["id"]: p for p in PARAMS}, (), seed=3)
        self.assertIsNone(d.behavior)
        d.set_level(0)
        d.update(DT, Inputs(phase="generating"))
        self.assertIsNone(d.pose_wanted())

    def test_a_pose_swap_takes_the_new_poses_variants(self):
        d = director()
        run(d, 6.0, level=3, phase="generating")
        d.set_sides("right", "left", pose_band="cheer", variants=())
        values = run(d, 1.0, level=3, phase="generating")
        self.assertIsNone(d.expr)
        self.assertFalse(any(v for k, v in values.items() if k.startswith("Var.expr")))
        self.assertTrue(all(math.isfinite(v) for v in values.values()))


class BehaviorUnitTests(unittest.TestCase):
    def test_entry_actions_wait_for_their_time(self):
        b = Behavior(1, ("cheer",), can=lambda g: True)
        b.update(DT, Inputs(phase="generating"), "boost")
        b.update(DT, Inputs(phase="generating", events=("newPeak",)), "boost")
        self.assertEqual(b.mood, "cheer")
        self.assertEqual(b.take_queue(), [])
        for _ in range(40):
            b.update(DT, Inputs(phase="generating"), "boost")
        self.assertEqual(b.take_queue(), ["hop"])


if __name__ == "__main__":
    unittest.main()
