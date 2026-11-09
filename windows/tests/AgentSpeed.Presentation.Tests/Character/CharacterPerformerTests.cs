using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentSpeed.Presentation.Character;

namespace AgentSpeed.Presentation.Tests.Character;

public class CharacterPackTests
{
    [Fact]
    public void LoadsPosesInBandOrderAndFallsBackToTheNearestLowerBand()
    {
        var rig = TinyPack.RigJson();
        var pack = CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", rig), ("boost", rig), ("redline", rig))));
        Assert.Equal([SpeedBand.Idle, SpeedBand.Boost, SpeedBand.Redline], pack.Poses.Select(p => p.Band));
        var performer = new CharacterPerformer(pack);
        Assert.Equal(0, performer.PoseIndex(SpeedBand.Idle));
        Assert.Equal(0, performer.PoseIndex(SpeedBand.Cruise));
        Assert.Equal(1, performer.PoseIndex(SpeedBand.Boost));
        Assert.Equal(1, performer.PoseIndex(SpeedBand.Overdrive));
        Assert.Equal(2, performer.PoseIndex(SpeedBand.Redline));
    }

    [Fact]
    public void RefusesPacksThatAreUnsafeOrIncomplete()
    {
        var rig = TinyPack.RigJson();
        Assert.Equal("missingIdlePose", Assert.Throws<PackLoadException>(() => CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("boost", rig))))).Kind);
        var outside = Assert.Throws<PackLoadException>(() => CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", rig)),
            tamper: m => m["poses"] = new JsonObject { ["idle"] = "../outside" })));
        Assert.Equal(("unsafePath", "../outside"), (outside.Kind, outside.Detail));
        var absolute = Assert.Throws<PackLoadException>(() => CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", rig)),
            tamper: m => m["preview"] = "/etc")));
        Assert.Equal(("unsafePath", "/etc"), (absolute.Kind, absolute.Detail));
        var backslash = Assert.Throws<PackLoadException>(() => CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", rig)),
            tamper: m => m["preview"] = "..\\preview.png")));
        Assert.Equal("unsafePath", backslash.Kind);
        var drive = Assert.Throws<PackLoadException>(() => CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", rig)),
            tamper: m => m["preview"] = "C:preview.png")));
        Assert.Equal("unsafePath", drive.Kind);
        var badId = Assert.Throws<PackLoadException>(() => CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", rig)), id: "Bad ID")));
        Assert.Equal(("badID", "Bad ID"), (badId.Kind, badId.Detail));
        var version = Assert.Throws<PackLoadException>(() => CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", rig)),
            tamper: m => m["version"] = 2)));
        Assert.Equal("unsupported", version.Kind);
    }

    [Fact]
    public void RefusesSymbolicLinksThatLeaveThePack()
    {
        var rig = TinyPack.RigJson();
        var root = TinyPack.Make(TinyPack.Bands(("idle", rig)));
        var outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.Delete(Path.Combine(root, "preview.png"));
        try
        {
            File.CreateSymbolicLink(Path.Combine(root, "preview.png"), outside);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        var error = Assert.Throws<PackLoadException>(() => CharacterPack.Load(root));
        Assert.Equal(("unsafePath", "preview.png"), (error.Kind, error.Detail));
    }

    [Fact]
    public void ComparesTheMinimumAppVersion()
    {
        var rig = TinyPack.RigJson();
        var pack = CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", rig)), tamper: m => m["minApp"] = "1.7.0"));
        Assert.True(pack.Manifest.PlayableByApp("1.7.0"));
        Assert.True(pack.Manifest.PlayableByApp("1.10"));
        Assert.False(pack.Manifest.PlayableByApp("1.6.3"));
    }
}

public class CharacterPerformerTests
{
    [Fact]
    public void PosesAlignTheirFeetAndFaceToFeetDistance()
    {
        var idle = TinyPack.RigJson(feetY: 90, faceTop: 10);
        var tall = TinyPack.RigJson(feetY: 95, faceTop: -45);
        var performer = new CharacterPerformer(CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", idle), ("boost", tall)))));
        Assert.Equal(1, performer.Placements[0].Scale);
        Assert.True(Math.Abs(performer.Placements[1].Scale - 70.0 / 130.0) < 1e-12);
        Assert.Equal((200.0, 300.0), performer.Placements[1].Screen(50, 95, (200, 300), 2));
    }

    [Fact]
    public void AJumpWithTuckedLegsKeepsItsSizeByTheMiddleRatio()
    {
        var idle = TinyPack.RigJson(feetY: 90, faceTop: 10);
        var jump = TinyPack.RigJson(feetY: 60, faceTop: 10);
        TinyPack.SetLandmark(idle, "torsoLength", 50);
        TinyPack.SetLandmark(jump, "torsoLength", 48);
        var performer = new CharacterPerformer(CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", idle), ("redline", jump)))));
        Assert.True(Math.Abs(performer.Placements[1].Scale - 50.0 / 48.0) < 1e-12);
        Assert.Equal(70.0 / 40.0, CharacterPerformer.PoseScale([70, 28, null], [40, 28, null]));
        Assert.Equal(1, CharacterPerformer.PoseScale([null, 28, 50], [null, 28, 50]));
    }

    [Fact]
    public void SwapsThePoseEdgeOnWhenTheBandChanges()
    {
        var rig = TinyPack.RigJson();
        var other = TinyPack.RigJson(objectSide: "right");
        other["name"] = "other";
        var performer = new CharacterPerformer(CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", rig), ("boost", other)))));
        var spin = performer.Director.Space.Slot("Spin")!.Value;
        for (var i = 0; i < 60; i++)
        {
            performer.Step(1.0 / 60, 0);
        }

        Assert.Equal(0, performer.ActivePose);
        double? swappedAt = null;
        for (var i = 0; i < 60; i++)
        {
            performer.Step(1.0 / 60, 6);
            if (performer.Events.Contains(PerformerEvent.PoseSwapped(SpeedBand.Boost)))
            {
                swappedAt = performer.Values[spin];
            }
        }

        Assert.Equal(1, performer.ActivePose);
        Assert.Equal(0.75, swappedAt);
        Assert.Equal(CharacterSide.Right, performer.Director.Book);
        Assert.Equal(0, performer.Values[spin]);
    }

    [Fact]
    public void TurnsFromOneCharacterToAnotherEdgeOn()
    {
        var rig = TinyPack.RigJson();
        var a = new CharacterPerformer(CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", rig)), id: "a")));
        var b = new CharacterPerformer(CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", rig), ("boost", rig)), id: "b")));
        var stage = new CharacterStage(a);
        for (var i = 0; i < 30; i++)
        {
            stage.Step(1.0 / 60, 6);
        }

        stage.SwitchTo(b);
        int? swapFrame = null;
        for (var n = 0; n < 60; n++)
        {
            stage.Step(1.0 / 60, 6);
            if (stage.Swapped)
            {
                swapFrame = n;
                Assert.Equal("b", stage.Current.Pack.Id);
                Assert.Equal(1, stage.Current.ActivePose);
                Assert.Equal(0.75, stage.Current.Values[stage.Current.Director.Space.Slot("Spin")!.Value]);
            }
        }

        Assert.NotNull(swapFrame);
        Assert.InRange(swapFrame!.Value, 11, 13);
        Assert.Null(stage.Incoming);
    }

    [Fact]
    public void StepsWithoutAllocatingOnceWarm()
    {
        var performer = new CharacterPerformer(CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", TinyPack.RigJson())))));
        var hud = new CharacterHud();
        var layout = performer.HudLayout((300, 600), 2, 1);
        (double, double, double)[] agents = [(217, 119, 87)];
        for (var n = 0; n < 240; n++)
        {
            performer.Step(1.0 / 60, 3);
            hud.Update(1.0 / 60, n / 60.0, 40, 0.3, 0.2, "x");
            hud.Layout(1.0 / 60, layout, agents, 20, (360, 300));
        }

        // A per-frame allocation shows up in every quiet window; a one-time runtime allocation (tiering) in one or two.
        var quiet = new List<long>();
        for (var w = 0; w < 40; w++)
        {
            var starts = performer.Director.MotionStarts;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var n = 0; n < 20; n++)
            {
                performer.Step(1.0 / 60, 3);
                hud.Update(1.0 / 60, 2 + n / 60.0, 40, 0.3, 0.2, "x");
                hud.Layout(1.0 / 60, layout, agents, 20, (360, 300));
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (performer.Director.MotionStarts == starts)
            {
                quiet.Add(allocated);
            }
        }

        Assert.Contains(0L, quiet);
        Assert.True(quiet.Count(bytes => bytes > 0) <= 2, $"allocations in quiet windows: {string.Join(", ", quiet)}");
        Assert.All(quiet, bytes => Assert.True(bytes < 1024, $"{bytes} bytes allocated in a window without new actions"));
    }

    [Fact]
    public void PlaysARealPackQuickly()
    {
        if (Environment.GetEnvironmentVariable("AGENT_SPEED_CHARACTER_PACK") is not { Length: > 0 } path)
        {
            return;
        }

        var performer = new CharacterPerformer(CharacterPack.Load(path));
        var hud = new CharacterHud();
        int[] levels = [0, 2, 6, 10, 13, 7, 1, 0];
        (double, double, double)[] agents = [(217, 119, 87)];
        const int frames = 60 * 40;
        var clock = Stopwatch.StartNew();
        for (var n = 0; n < frames; n++)
        {
            var level = levels[Math.Min(levels.Length - 1, n / 300)];
            performer.Step(1.0 / 60, level);
            var layout = performer.HudLayout((300, 600), 0.4, 0.5);
            hud.Update(1.0 / 60, n / 60.0, level * 30, level / 13.0, performer.Intensity, "t");
            hud.Layout(1.0 / 60, layout, agents, 20, (360, 300));
        }

        var perFrame = clock.Elapsed.TotalMilliseconds / frames;
        Assert.True(perFrame < 2, $"{perFrame:F3} ms per frame");
    }
}

public class CharacterHudTests
{
    [Fact]
    public void RingLightsFillWithHeatAndSplitFrontAndBack()
    {
        var hud = new CharacterHud();
        var layout = new HudLayout((300, 600, 200, 34), (300, 400, 220, 36), 700, 1);
        hud.Update(1.0 / 60, 1, 120, 0.5, 0.5, "x");
        hud.Layout(1.0 / 60, layout, [], 20, (360, 300));
        Assert.Contains(hud.Front.ToArray(), d => d.Y >= 600);
        Assert.Contains(hud.Back.ToArray(), d => d.Y < 600 && d.Additive);
        Assert.True(hud.Odometer.Digits.Count >= 1);
    }

    [Fact]
    public void PeakPillPopsAndFades()
    {
        var hud = new CharacterHud();
        var layout = new HudLayout((300, 600, 200, 34), (300, 400, 220, 36), 700, 1);
        hud.CelebratePeak();
        var seen = false;
        for (var n = 0; n < 180; n++)
        {
            hud.Update(1.0 / 60, n / 60.0, 300, 0.8, 0.8, "x");
            hud.Layout(1.0 / 60, layout, [], 20, (360, 300));
            seen |= hud.Peak is not null;
        }

        Assert.True(seen);
        Assert.Null(hud.Peak);
    }
}

/// <summary>Reduce Motion and the effects levels (DESIGN 2a.6): calm swaps crossfade, airborne actions are skipped, amplitude pulls motion toward rest.</summary>
public class CharacterCalmTests
{
    [Fact]
    public void CalmPoseSwapsCrossfadeWithoutTurning()
    {
        var rig = TinyPack.RigJson();
        var other = TinyPack.RigJson(objectSide: "right");
        other["name"] = "other";
        var performer = new CharacterPerformer(CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", rig), ("boost", other))))) { Calm = true };
        var spin = performer.Director.Space.Slot("Spin")!.Value;
        for (var i = 0; i < 30; i++)
        {
            performer.Step(1.0 / 60, 0);
        }

        var maxSpin = 0.0;
        var faded = false;
        for (var i = 0; i < 60; i++)
        {
            performer.Step(1.0 / 60, 6);
            maxSpin = Math.Max(maxSpin, performer.Values[spin]);
            faded |= performer.FadingPose == 0;
        }

        Assert.Equal(1, performer.ActivePose);
        Assert.True(faded);
        Assert.Equal(0, maxSpin);
        Assert.Null(performer.FadingPose);
        Assert.Equal(1, performer.FadeProgress);
    }

    [Fact]
    public void CalmCharacterChangesCrossfade()
    {
        var rig = TinyPack.RigJson();
        var a = new CharacterPerformer(CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", rig)), id: "a")));
        var b = new CharacterPerformer(CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", rig)), id: "b")));
        var stage = new CharacterStage(a) { Calm = true };
        stage.Step(1.0 / 60, 2);
        stage.SwitchTo(b);
        stage.Step(1.0 / 60, 2);
        Assert.True(stage.Swapped);
        Assert.Equal("b", stage.Current.Pack.Id);
        Assert.Equal("a", stage.Outgoing?.Pack.Id);
        for (var i = 0; i < 30; i++)
        {
            stage.Step(1.0 / 60, 2);
        }

        Assert.Null(stage.Outgoing);
    }

    [Fact]
    public void GroundedDirectorNeverLeavesTheGround()
    {
        var parameters = CharacterFixture.Json("director.json").GetProperty("params").EnumerateArray().Select(CharacterParam.Decode).ToArray();
        var director = new CharacterDirector(parameters, 7, CharacterSide.Right, CharacterSide.Left, 1, celebrate: false) { Grounded = true };
        var played = new HashSet<string>();
        for (var n = 0; n < 60 * 60; n++)
        {
            director.SetLevel(Math.Min(13, n / 300 % 14));
            director.Update(1.0 / 60);
            if (director.ActiveGesture is { } g)
            {
                played.Add(g);
            }
        }

        Assert.Empty(played.Intersect(["hop", "jump", "hops", "spin", "step", "run", "jumpjoy"]));
        Assert.NotEmpty(played);
    }

    [Fact]
    public void ZeroAmplitudeKeepsTheRestPoseButBreathes()
    {
        var performer = new CharacterPerformer(CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", TinyPack.RigJson()))))) { Amplitude = 0 };
        var space = performer.Director.Space;
        var breath = space.Slot("Breath")!.Value;
        var breathSeen = 0.0;
        for (var i = 0; i < 240; i++)
        {
            performer.Step(1.0 / 60, 12);
            breathSeen = Math.Max(breathSeen, performer.Values[breath]);
            foreach (var name in new[] { "PosX", "PosY", "Squash", "Lean" })
            {
                Assert.True(Math.Abs(performer.Values[space.Slot(name)!.Value]) < 1e-9);
            }
        }

        Assert.True(breathSeen > 0.3);
    }
}
