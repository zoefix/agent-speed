using System.Text.Json.Nodes;
using AgentSpeed.Presentation.Character;

namespace AgentSpeed.Presentation.Tests.Character;

/// <summary>Living packs on disk: the tiny rig with face patches, and mood poses after the bands.</summary>
internal static class LivingPack
{
    public static readonly string[] Patches = ["expr-smile", "expr-thinking", "mouth-a", "mouth-o", "gaze-left", "gaze-right", "gaze-closed"];

    /// <summary>The tiny rig with face patches as parameters and <c>patch</c> variants.</summary>
    public static JsonObject RigJson(IReadOnlyList<string>? patches = null, double faceTop = 10)
    {
        patches ??= Patches;
        var rig = TinyPack.RigJson(faceTop: faceTop);
        var parameters = rig["params"]!.AsArray();
        var variants = new JsonArray();
        foreach (var id in patches)
        {
            parameters.Add(new JsonObject { ["id"] = $"Var.{id}", ["min"] = 0, ["max"] = 1, ["default"] = 0 });
            var region = id.StartsWith("expr", StringComparison.Ordinal) ? "face" : id.StartsWith("mouth", StringComparison.Ordinal) ? "mouth" : "eyes";
            variants.Add(new JsonObject { ["id"] = id, ["kind"] = "patch", ["param"] = $"Var.{id}", ["region"] = region });
        }

        rig["variants"] = variants;
        return rig;
    }

    /// <summary>A pack with an idle and a cruise band pose and the given mood poses; <paramref name="extra"/> adds manifest <c>states</c> entries.</summary>
    public static string Make(IReadOnlyList<string> states, IReadOnlyDictionary<string, string>? extra = null, JsonObject? idle = null,
        JsonObject? motion = null, string id = "tiny")
    {
        var rig = idle ?? RigJson();
        var root = TinyPack.Make(TinyPack.Bands(("idle", rig), ("cruise", rig.DeepClone().AsObject())), id, manifest =>
        {
            var map = new JsonObject();
            foreach (var state in states)
            {
                map[state] = $"poses/{state}";
            }

            foreach (var (key, value) in extra ?? new Dictionary<string, string>())
            {
                map[key] = value;
            }

            manifest["states"] = map;
            if (motion is not null)
            {
                manifest["motion"] = motion;
            }
        });
        foreach (var state in states)
        {
            var dir = Path.Combine(root, "poses", state);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "rig.json"), TinyPack.RigJson(faceTop: 20).ToJsonString());
            File.WriteAllBytes(Path.Combine(dir, "atlas.png"), [0]);
        }

        return root;
    }
}

/// <summary>
/// A living pack on the stage (spec/CHARACTERS.md, Behavior; section 6 with mood poses): mood poses after the bands, the
/// mood's pose shown through the same swap, the shown pose's face patches, and the inputs ignored by other packs.
/// </summary>
public class LivingPerformerTests
{
    private const double Dt = 1.0 / 60;

    [Fact]
    public void MoodPosesLoadAfterTheBandsAndNeverStandForABand()
    {
        var pack = CharacterPack.Load(LivingPack.Make(["sleepy", "think"]));
        Assert.Equal(["idle", "cruise", "think", "sleepy"], pack.Poses.Select(p => p.Name));
        Assert.Equal(["think", "sleepy"], pack.States);
        Assert.Equal(SpeedBand.Idle, pack.Poses[2].Band);
        Assert.True(pack.IsLiving);
        var performer = new CharacterPerformer(pack);
        Assert.True(performer.Living && performer.Director.IsLiving);
        Assert.Equal(1, performer.PoseIndex(SpeedBand.Redline));
    }

    [Fact]
    public void AMoodPoseLeadingOutsideThePackIsRefusedAndAMissingOneSkipped()
    {
        var outside = Assert.Throws<PackLoadException>(() => CharacterPack.Load(LivingPack.Make([], new Dictionary<string, string> { ["think"] = "../think" })));
        Assert.Equal(("unsafePath", "../think"), (outside.Kind, outside.Detail));
        var unknown = Assert.Throws<PackLoadException>(() => CharacterPack.Load(LivingPack.Make([], new Dictionary<string, string> { ["dance"] = "/etc" })));
        Assert.Equal("unsafePath", unknown.Kind);
        var pack = CharacterPack.Load(LivingPack.Make(["think"], new Dictionary<string, string> { ["focus"] = "poses/focus" }));
        Assert.Equal(["think"], pack.States);
    }

    [Fact]
    public void AnUnreadableMoodPoseIsSkippedAndBrokenStatesAreIgnored()
    {
        var root = LivingPack.Make(["think", "wait"]);
        File.WriteAllText(Path.Combine(root, "poses", "think", "rig.json"), "{ not json");
        File.Delete(Path.Combine(root, "poses", "wait", "atlas.png"));
        var pack = CharacterPack.Load(root);
        Assert.Empty(pack.States);
        Assert.Equal(2, pack.Poses.Count);
        Assert.True(pack.IsLiving);

        var odd = CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", TinyPack.RigJson())), tamper: m => m["states"] = new JsonObject { ["think"] = 3 }));
        Assert.Empty(odd.Manifest.States);
        Assert.False(odd.IsLiving);
    }

    [Fact]
    public void APackWithoutPatchesOrStatesIgnoresTheInputs()
    {
        var rig = TinyPack.RigJson();
        var pack = CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", rig), ("cruise", rig))));
        Assert.False(pack.IsLiving);
        var plain = new CharacterPerformer(pack, seed: 3);
        var fed = new CharacterPerformer(pack, seed: 3);
        var inputs = new BehaviorInputs { Phase = AgentActivity.Generating, Wait = 12, Cursor = (0.3, 0.1), Over = true };
        for (var n = 0; n < 600; n++)
        {
            var level = n < 300 ? 0 : 3;
            plain.Step(Dt, level);
            fed.Step(Dt, level, inputs);
        }

        Assert.Equal(plain.Values, fed.Values);
        Assert.Null(fed.Director.PoseWanted());
        Assert.Null(fed.Director.Behavior);
    }

    [Fact]
    public void RestingShowsTheSleepyPoseAtOnceAndWorkWakesHer()
    {
        var pack = CharacterPack.Load(LivingPack.Make(["sleepy", "greet", "think"]));
        var performer = new CharacterPerformer(pack);
        var spin = performer.Director.Space.Slot("Spin")!.Value;
        var sleepy = pack.Poses.Select((p, i) => (p, i)).First(x => x.p.State == "sleepy").i;
        performer.Step(Dt, 0, new BehaviorInputs { Phase = AgentActivity.Idle, Idle = 900, Hour = 1 });
        Assert.Equal(CharacterMood.Rest, performer.Director.Mood);
        Assert.Equal(sleepy, performer.ActivePose);
        Assert.Equal("sleepy", performer.Director.PoseName);
        Assert.Null(performer.Director.PoseBand);

        // The first frame shows the mood pose without a turn.
        Assert.DoesNotContain(performer.Events, e => e.Kind == PerformerEventKind.PoseSwapped);
        Assert.Equal(0, performer.Values[spin]);

        var seen = new List<string>();
        var request = new BehaviorInputs { Phase = AgentActivity.Generating, Wait = 0, Hour = 1 };
        for (var n = 0; n < 60 * 14; n++)
        {
            request.Wait = n / 60.0;
            performer.Step(Dt, 0, request);
            var name = pack.Poses[performer.ActivePose].Name;
            if (seen.Count == 0 || seen[^1] != name)
            {
                seen.Add(name);
            }
        }

        // Waking up greets in the greet pose, the long wait for the first token thinks in the think pose.
        Assert.Equal(["sleepy", "greet", "idle", "think"], seen);
    }

    [Fact]
    public void ABandChangeSwapsNothingWhileAMoodPoseShows()
    {
        var pack = CharacterPack.Load(LivingPack.Make(["sleepy"]));
        var performer = new CharacterPerformer(pack);
        var resting = new BehaviorInputs { Phase = AgentActivity.Idle, Idle = 900, Hour = 1 };
        var swaps = 0;
        for (var n = 0; n < 240; n++)
        {
            performer.Step(Dt, n < 60 ? 0 : 3, resting);
            swaps += performer.Events.Count(e => e.Kind == PerformerEventKind.PoseSwapped);
        }

        Assert.Equal("sleepy", pack.Poses[performer.ActivePose].Name);
        Assert.Equal(0, swaps);
        Assert.Equal(performer.ActivePose, performer.WantedPose);
    }

    [Fact]
    public void TheFaceCenterIsTheShownPosesFaceBoxOnScreen()
    {
        var pack = CharacterPack.Load(LivingPack.Make([]));
        var performer = new CharacterPerformer(pack);
        var (x, y) = performer.FaceCenter((200, 400), 2);

        // Face box [40, 10, 60, 30], feet (50, 90): the face center is right above the feet, 70 px up.
        Assert.True(Math.Abs(x - 200) < 1e-9 && Math.Abs(y - (400 - 70 * 2)) < 1e-9, $"{x}, {y}");
    }

    [Fact]
    public void TheCursorTurnsHerHeadAndHerEyesTakeTheGazePatch()
    {
        var pack = CharacterPack.Load(LivingPack.Make([]));
        var performer = new CharacterPerformer(pack);
        var angleX = performer.Director.Space.Slot("AngleX")!.Value;
        var looking = new BehaviorInputs { Phase = AgentActivity.Idle, Idle = 30, Cursor = (0.9, 0.05) };
        var turned = 0.0;
        for (var n = 0; n < 60 * 3; n++)
        {
            performer.Step(Dt, 0, looking);
            turned = Math.Max(turned, performer.Values[angleX]);
        }

        Assert.True(turned > 0.45, $"head toward the cursor on her right: {turned}");
        Assert.Equal("right", performer.Director.Behavior?.Gaze);
        Assert.True(performer.Director.GazePatch == "right" || performer.Director.Expression is not null);
        looking.Cursor = null;
        for (var n = 0; n < 60 * 3; n++)
        {
            performer.Step(Dt, 0, looking);
        }

        Assert.Equal(0, performer.Director.Behavior?.LookWeight);
    }

    [Fact]
    public void SheActsWhileHerHeadFollowsAMovingCursor()
    {
        // Every idle action excluded, so only the look and the face can make her act.
        var motion = new JsonObject
        {
            ["exclude"] = new JsonArray("open", "chin", "hairtuck", "hug", "yawn", "stretch", "lookaround", "tilt", "foottap", "ponder", "step", "bow"),
        };
        var performer = new CharacterPerformer(CharacterPack.Load(LivingPack.Make([], motion: motion)));
        var inputs = new BehaviorInputs { Phase = AgentActivity.Idle, Idle = 30, Cursor = (0.5, 0.1) };
        for (var n = 0; n < 60 * 3; n++)
        {
            performer.Step(Dt, 0, inputs);
        }

        Assert.Null(performer.Director.ActiveGesture);
        Assert.Equal(1, performer.Director.Behavior!.LookWeight);
        Assert.False(performer.IsActing, "a still cursor, a settled look and face");
        for (var n = 0; n < 30; n++)
        {
            inputs.Cursor = (0.5 + 0.005 * (n + 1), 0.1);
            performer.Step(Dt, 0, inputs);
            Assert.True(performer.IsActing, $"following the moving cursor, frame {n}");
        }

        var settled = -1;
        for (var n = 0; n < 60; n++)
        {
            performer.Step(Dt, 0, inputs);
            if (!performer.IsActing && settled < 0)
            {
                settled = n;
            }
        }

        Assert.InRange(settled, 30, 40);
        inputs.Cursor = null;
        performer.Step(Dt, 0, inputs);
        Assert.True(performer.IsActing, "the look lets go");
    }

    [Fact]
    public void SheTalksInMouthShapesWhileOutputFlows()
    {
        var pack = CharacterPack.Load(LivingPack.Make([]));
        var performer = new CharacterPerformer(pack);
        var a = performer.Director.Space.Slot("Var.mouth-a")!.Value;
        var o = performer.Director.Space.Slot("Var.mouth-o")!.Value;
        var shapes = new HashSet<string>();
        var mixed = false;
        var talked = false;
        for (var n = 0; n < 60 * 20; n++)
        {
            performer.Step(Dt, 3, new BehaviorInputs { Phase = AgentActivity.Generating });
            if (performer.Director.MouthShape is { } shape)
            {
                shapes.Add(shape);
            }

            talked |= performer.Director.IsTalking;
            var (va, vo) = (performer.Values[a], performer.Values[o]);
            mixed |= va > 0 && (va < 1 || vo > 0);
        }

        Assert.True(talked);
        Assert.Equal(["a", "o"], shapes.Order());
        Assert.False(mixed, "mouth shapes switch on a frame and never blend");
    }

    [Fact]
    public void AClickPokesHerAndKeepsTheShownPose()
    {
        var pack = CharacterPack.Load(LivingPack.Make([]));
        var performer = new CharacterPerformer(pack);
        for (var n = 0; n < 60; n++)
        {
            performer.Step(Dt, 0, new BehaviorInputs { Phase = AgentActivity.Idle, Idle = 30 });
        }

        performer.Step(Dt, 0, new BehaviorInputs { Phase = AgentActivity.Idle, Idle = 30, Cursor = (0, 0.3), Over = true, Click = true });
        Assert.Equal(CharacterMood.Poke, performer.Director.Mood);
        Assert.Equal<MoodPose?>(MoodPose.Keep, performer.Director.PoseWanted());
        Assert.Equal("surprised", performer.Director.Behavior!.Face(null, null, SpeedBand.Idle));
    }

    [Fact]
    public void TheCharacterLeavingTheStageIsNeverPokedOrHovered()
    {
        var a = new CharacterPerformer(CharacterPack.Load(LivingPack.Make([], id: "a")));
        var b = new CharacterPerformer(CharacterPack.Load(LivingPack.Make([], id: "b")));
        var stage = new CharacterStage(a) { Calm = true };
        var idle = new BehaviorInputs { Phase = AgentActivity.Idle, Idle = 30 };
        for (var n = 0; n < 30; n++)
        {
            stage.Step(Dt, 0, idle);
        }

        stage.SwitchTo(b);
        stage.Step(Dt, 0, idle);
        Assert.Same(b, stage.Current);
        Assert.Same(a, stage.Outgoing);
        stage.Step(Dt, 0, idle with { Cursor = (0, 0.3), Over = true, Click = true });
        Assert.Equal(CharacterMood.Poke, b.Director.Mood);
        Assert.Equal(CharacterMood.Wait, a.Director.Mood);
        Assert.False(a.Director.Behavior!.Inputs.Over);
        Assert.Equal((0.0, 0.3), a.Director.Behavior.Inputs.Cursor!.Value);
    }

    [Fact]
    public void PlaysARealLivingPackThroughTheSessionQuickly()
    {
        if (Environment.GetEnvironmentVariable("AGENT_SPEED_LIVING_PACK") is not { Length: > 0 } path)
        {
            return;
        }

        var pack = CharacterPack.Load(path);
        Assert.True(pack.IsLiving);
        var performer = new CharacterPerformer(pack);
        var session = new BehaviorSession(CharacterFixture.Json("behavior.json").GetProperty("session"));
        var frames = (int)(session.Seconds * 60);
        var inputs = Enumerable.Range(0, frames).Select(n => session.Inputs(n / 60.0, Dt)).ToArray();
        var poses = new HashSet<string>();
        var gestures = new HashSet<string>();
        var faces = new HashSet<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var n = 0; n < frames; n++)
        {
            performer.Step(Dt, session.Level(n / 60.0), inputs[n]);
            poses.Add(pack.Poses[performer.ActivePose].Name);
            if (performer.Director.ActiveGesture is { } g)
            {
                gestures.Add(g);
            }

            if (performer.Director.Expression is { } face)
            {
                faces.Add(face);
            }
        }

        var perFrame = clock.Elapsed.TotalMilliseconds / frames;
        Assert.True(perFrame < 2, $"{perFrame:F3} ms per frame");
        Assert.Contains("sleepy", poses);
        Assert.True(poses.Count >= 4, string.Join(", ", poses));
        Assert.True(faces.Count >= 3, string.Join(", ", faces));
        Assert.True(gestures.Overlaps(["wave", "chin", "peace", "thumbsup", "hairtuck", "idea", "fist", "hip", "cheek"]), string.Join(", ", gestures));
    }

    [Fact]
    public void TalkingAndFollowingTheCursorAllocateNothingOnceWarm()
    {
        string[] patches = ["expr-smile", "expr-wink", "expr-thinking", "mouth-a", "mouth-o", "mouth-closed", "gaze-left", "gaze-right", "gaze-closed"];
        var performer = new CharacterPerformer(CharacterPack.Load(LivingPack.Make(["think"], idle: LivingPack.RigJson(patches))));
        var inputs = new BehaviorInputs { Phase = AgentActivity.Generating };
        var t = 0.0;
        var talked = false;
        void Step()
        {
            t += Dt;
            var cursor = (0.5 * Math.Sin(t), 0.2 * Math.Cos(0.7 * t));
            inputs.Cursor = cursor;
            inputs.Over = Math.Abs(cursor.Item1) < 0.2;
            performer.Step(Dt, 3, inputs);
            talked |= performer.Director.IsTalking;
            _ = performer.IsActing;
        }

        // A minute first: each path's first run initializes its static tables once.
        for (var n = 0; n < 3600; n++)
        {
            Step();
        }

        Assert.True(talked);
        var quiet = new List<long>();
        for (var w = 0; w < 60; w++)
        {
            var starts = performer.Director.MotionStarts;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var n = 0; n < 20; n++)
            {
                Step();
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (performer.Director.MotionStarts == starts)
            {
                quiet.Add(allocated);
            }
        }

        Assert.NotEmpty(quiet);
        Assert.Contains(0L, quiet);
        Assert.True(quiet.Count(bytes => bytes > 0) <= 2, $"allocations in quiet windows: {string.Join(", ", quiet)}");
        Assert.All(quiet, bytes => Assert.True(bytes < 1024, $"{bytes} bytes allocated in a window without new actions"));
    }
}
