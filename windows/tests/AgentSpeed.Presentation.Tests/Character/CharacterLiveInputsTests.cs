using AgentSpeed.Core;
using AgentSpeed.Presentation.Character;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Orb.Gpu.Styles;
using AgentSpeed.Presentation.Settings;
using Xunit.Abstractions;

namespace AgentSpeed.Presentation.Tests.Character;

/// <summary>
/// The inputs of a living character in the app (CHARACTERS 5, <i>Behavior</i>, <i>Inputs</i>): the orb's agents into her
/// live inputs, the pointer onto her canvas and into her heights, the hit capsule, the events of each frame, and the frame
/// harness's scripted session.
/// </summary>
public class CharacterLiveInputsTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private sealed class FakePointer : IOrbCharacterPointer
    {
        public (double X, double Y)? PointerFraction { get; set; }

        public bool IsDragging { get; set; }
    }

    private static AgentStatus Agent(string id, AgentPhase phase, DateTimeOffset? inFlight = null, DateTimeOffset? lastEnd = null) =>
        new(id, SourceKind.ClaudeCode, "p", null, phase, false, Rate.Zero, null, Start)
        {
            InFlightSince = inFlight,
            LastResponseEnd = lastEnd,
        };

    private static SpeedSnapshot Snapshot(DateTimeOffset at, params AgentStatus[] agents) => SpeedSnapshot.Empty with
    {
        Timestamp = at,
        State = MonitorState.Running,
        Agents = agents,
        DayStart = Start.Date,
    };

    private static CharacterPerformer Living() =>
        new(CharacterPack.Load(LivingPack.Make([])));

    private static OrbGpuContext Context(double size = 296) =>
        new(size, 2, OrbTokens.For(OrbAppearance.Dark), new EffectsPolicy(EffectsIntensity.Standard, false));

    [Fact]
    public void TheFeedHandsTheOrbsPhaseWaitAndIdleToHerInputs()
    {
        var feed = new OrbFeed("orb") { Character = true };
        feed.Configure(null, SpeedMetric.Output, 400, 0);
        feed.Update(Snapshot(Start.AddSeconds(10), Agent("a", AgentPhase.Generating, inFlight: Start, lastEnd: Start.AddSeconds(-30)),
            Agent("b", AgentPhase.Idle, lastEnd: Start.AddSeconds(-30))), 1010);
        var live = feed.CharacterLive;
        Assert.Equal(AgentActivity.Generating, live.Phase);
        Assert.Equal(1000, live.WaitingSince!.Value, 9);
        Assert.Equal(1010, live.IdleSince, 9);
        Assert.Equal(Start.AddSeconds(10).ToLocalTime().Hour, live.Hour);

        feed.Update(Snapshot(Start.AddSeconds(30), Agent("a", AgentPhase.Idle, lastEnd: Start.AddSeconds(20)),
            Agent("b", AgentPhase.Idle, lastEnd: Start.AddSeconds(-30))), 1030);
        Assert.Equal(AgentActivity.Idle, live.Phase);
        Assert.Null(live.WaitingSince);
        Assert.Equal(1020, live.IdleSince, 9);

        feed.NoteResponseEnd(Start.AddSeconds(25));
        Assert.Equal(1025, live.IdleSince, 9);
        feed.NoteResponseEnd(Start.AddSeconds(22));
        Assert.Equal(1025, live.IdleSince, 9);
    }

    [Fact]
    public void AnOrbThatIsNoCharacterKeepsNoActivity()
    {
        var feed = new OrbFeed("orb");
        feed.Configure(null, SpeedMetric.Output, 400, 0);
        feed.Update(Snapshot(Start.AddSeconds(10), Agent("a", AgentPhase.Generating, inFlight: Start)), 1010);
        Assert.Equal(AgentActivity.Idle, feed.CharacterLive.Phase);
        feed.NoteResponseEnd(Start.AddSeconds(20));
        Assert.Equal(0, feed.CharacterLive.IdleSince);
    }

    [Fact]
    public void ResponsesOfOtherSourcesNeverRestartASubsetOrbsIdleTime()
    {
        var feed = new OrbFeed("orb") { Character = true };
        feed.Configure(SourceSet.Of(SourceKind.Codex), SpeedMetric.Output, 400, 0);
        var other = new ResponseSummary(SourceKind.ClaudeCode, "p", null, 10, 10, null, null, Start.AddSeconds(5));
        feed.Update(Snapshot(Start.AddSeconds(10)) with { LastResponse = other }, 1010);
        Assert.Equal(1010, feed.CharacterLive.IdleSince, 9);
        var own = other with { Source = SourceKind.Codex, EndedAt = Start.AddSeconds(15) };
        feed.Update(Snapshot(Start.AddSeconds(20)) with { LastResponse = own }, 1020);
        Assert.Equal(1015, feed.CharacterLive.IdleSince, 9);
    }

    [Fact]
    public void APointOnTheWindowLandsOnHerCanvasWithTheContentScaleUndone()
    {
        Assert.Equal((0.0, 0.0), OrbCharacterLive.CanvasPoint((0, 0), 300, 1));
        Assert.Equal((150.0, 150.0), OrbCharacterLive.CanvasPoint((0.5, 0.5), 300, 1.03));
        var right = OrbCharacterLive.CanvasPoint((253.0 / 300, 0.5), 300, 1.03);
        Assert.Equal(250, right.X, 9);
        Assert.Equal(150, right.Y, 9);
    }

    [Fact]
    public void TheCursorIsInHerHeightsFromHerFaceWithinTwoHeights()
    {
        var c = CharacterDraw.Cursor((160, 130), (100, 100), 200)!.Value;
        Assert.Equal(0.3, c.X, 12);
        Assert.Equal(0.15, c.Y, 12);
        Assert.Null(CharacterDraw.Cursor((400, 400), (100, 100), 200));
        Assert.NotNull(CharacterDraw.Cursor((499, 100), (100, 100), 200));
        Assert.Null(CharacterDraw.Cursor((160, 130), (100, 100), 0));
    }

    [Fact]
    public void OverIsHerHitCapsuleMovedWithHerSideSteps()
    {
        const double d = 296;
        const double c = 222;
        Assert.True(CharacterDraw.OverCapsule((c, c), c, c, d, 0));
        Assert.False(CharacterDraw.OverCapsule((c + 0.3 * d, c), c, c, d, 0));
        Assert.True(CharacterDraw.OverCapsule((c + 0.3 * d, c), c, c, d, 0.3 * d));
        Assert.False(CharacterDraw.OverCapsule((c, c - 0.5 * d), c, c, d, 0));
        Assert.True(CharacterDraw.OverCapsule((c, c + 0.45 * d), c, c, d, 0), "the capsule sits 0.04 D below the disc center");
    }

    [Fact]
    public void EachFrameTakesTheLiveInputsAndFiresEachEventOnce()
    {
        var performer = Living();
        Assert.True(performer.Living);
        var draw = new CharacterDraw();
        var ctx = Context();
        var live = new OrbCharacterLive { Phase = AgentActivity.Generating, WaitingSince = 95, IdleSince = 40, Hour = 3 };
        live.NotePeak();
        var first = draw.BehaviorInputsFor(ctx, 100, live, performer)!.Value;
        Assert.Equal(AgentActivity.Generating, first.Phase);
        Assert.Equal(5, first.Wait);
        Assert.Equal(0, first.Idle);
        Assert.Equal(3, first.Hour);
        Assert.False(first.NewPeak, "an event before her first frame never fires");
        Assert.Null(first.Cursor);
        Assert.False(first.Over, "no window: no pointer");

        live.NotePeak();
        live.NoteTrouble();
        live.Phase = AgentActivity.Idle;
        var second = draw.BehaviorInputsFor(ctx, 101, live, performer)!.Value;
        Assert.True(second.NewPeak);
        Assert.True(second.Trouble);
        Assert.Null(second.Wait);
        Assert.Equal(61, second.Idle);
        var third = draw.BehaviorInputsFor(ctx, 102, live, performer)!.Value;
        Assert.False(third.NewPeak);
        Assert.False(third.Trouble);

        live.CelebratesPeaks = false;
        live.NotePeak();
        Assert.False(draw.BehaviorInputsFor(ctx, 103, live, performer)!.Value.NewPeak, "peaks cheer her only while they are celebrated");

        live.Scripted = true;
        live.ScriptedCursor = (0.2, 0.1);
        var scripted = draw.BehaviorInputsFor(ctx, 104, live, performer)!.Value;
        Assert.Equal(0.2, scripted.Cursor!.Value.X, 9);
        Assert.Equal(0.1, scripted.Cursor!.Value.Y, 9);
        live.ScriptedClick();
        Assert.False(draw.BehaviorInputsFor(ctx, 105, live, performer)!.Value.Click, "without her traced outline a click lands on nothing");
    }

    [Fact]
    public void ThePointerOfHerWindowIsReadUnlessADragMovesTheOrb()
    {
        var performer = Living();
        var draw = new CharacterDraw();
        var ctx = Context();
        var pointer = new FakePointer { PointerFraction = (0.5, 0.5) };
        var live = new OrbCharacterLive { Pointer = pointer };
        var inputs = draw.BehaviorInputsFor(ctx, 1, live, performer)!.Value;
        Assert.NotNull(inputs.Cursor);
        Assert.True(inputs.Over, "the canvas center is inside her capsule");

        pointer.IsDragging = true;
        inputs = draw.BehaviorInputsFor(ctx, 2, live, performer)!.Value;
        Assert.False(inputs.Over);

        pointer.PointerFraction = null;
        Assert.Null(draw.BehaviorInputsFor(ctx, 3, live, performer)!.Value.Cursor);
        pointer.PointerFraction = (0.99, 0.01);
        Assert.NotNull(draw.BehaviorInputsFor(ctx, 4, live, performer)!.Value.Cursor);
        pointer.PointerFraction = (3.2, 0.5);
        Assert.Null(draw.BehaviorInputsFor(ctx, 5, live, performer)!.Value.Cursor);
    }

    [Fact]
    public void APackWithoutPatchesOrMoodPosesGetsNoInputs()
    {
        var performer = new CharacterPerformer(CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", TinyPack.RigJson())))));
        Assert.False(performer.Living);
        var live = new OrbCharacterLive { Phase = AgentActivity.Generating };
        Assert.Null(new CharacterDraw().BehaviorInputsFor(Context(), 1, live, performer));
    }

    /// <summary>The whole path on an orb: her live inputs reach the director, and a click on her drawn figure pokes her.</summary>
    [Fact]
    public void TheOrbFeedsHerAndAClickOnHerFigurePokesHer()
    {
        var live = new OrbCharacterLive { Phase = AgentActivity.Generating };
        var (orb, _) = CharacterDrawTests.Create(CharacterPack.Load(LivingPack.Make([])));
        orb.SetCharacter(orb.Character! with { Live = live });
        var draw = Assert.IsType<CharacterDraw>(orb.Style);
        var time = 10.0;
        orb.SnapNext();
        for (var n = 0; n < 120; n++)
        {
            if (n % 30 == 0)
            {
                CharacterDrawTests.Feed(orb, time, 600);
            }

            orb.Render(time);
            time += 1 / 60.0;
        }

        var director = draw.Stage!.Current.Director;
        Assert.Equal(CharacterMood.Work, director.Mood);
        live.Scripted = true;
        live.ScriptedCursor = (0, 0.3);
        live.ScriptedClick();
        orb.Render(time);
        Assert.Equal(CharacterMood.Poke, director.Mood);
        Assert.Contains("mood=poke", draw.BehaviorTrace, StringComparison.Ordinal);
    }

    /// <summary>A living character's warm frame (talking, a moving pointer over her) allocates no more than an ordinary one's.</summary>
    [Fact]
    public void ALivingCharactersWarmFrameAllocatesNoMoreThanAnOrdinaryOnes()
    {
        static long Least(CharacterPack pack, bool living)
        {
            var pointer = new FakePointer();
            var live = living ? new OrbCharacterLive { Phase = AgentActivity.Generating, Pointer = pointer } : null;
            var (orb, _) = CharacterDrawTests.Create(pack);
            orb.SetCharacter(orb.Character! with { Live = live });
            var draw = Assert.IsType<CharacterDraw>(orb.Style);
            var time = 10.0;
            var n = 0;
            void Frame()
            {
                if (n++ % 30 == 0)
                {
                    CharacterDrawTests.Feed(orb, time, 600);
                }

                pointer.PointerFraction = (0.5 + 0.08 * Math.Sin(time * 1.3), 0.45 + 0.05 * Math.Cos(time * 0.7));
                orb.Render(time);
                time += 1 / 60.0;
            }

            orb.SnapNext();
            for (var i = 0; i < 1200; i++)
            {
                Frame();
            }

            var least = long.MaxValue;
            for (var w = 0; w < 90; w++)
            {
                var starts = draw.Stage!.Current.Director.MotionStarts;
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 20; i++)
                {
                    Frame();
                }

                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                if (draw.Stage!.Current.Director.MotionStarts == starts)
                {
                    least = Math.Min(least, allocated);
                }
            }

            return least;
        }

        var ordinary = Least(CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", TinyPack.RigJson()), ("cruise", TinyPack.RigJson())))), false);
        var living = Least(CharacterPack.Load(LivingPack.Make(["think"])), true);
        output.WriteLine($"least over 20 quiet frames: ordinary {ordinary} B, living {living} B");
        Assert.NotEqual(long.MaxValue, living);
        Assert.True(living <= ordinary, $"living {living} B, ordinary {ordinary} B over 20 frames without new actions");
    }

    [Fact]
    public void TheHarnessSessionReadsTheFixtureScript()
    {
        var doc = CharacterFixture.Json("behavior.json");
        var script = CharacterSessionScript.Parse(doc.GetProperty("session").GetRawText())!;
        Assert.Equal(1, script.Hour);
        Assert.Equal(AgentActivity.Idle, script.PhaseAt(1).Phase);
        Assert.Equal(901, script.IdleAt(1), 9);
        Assert.Equal(AgentActivity.Generating, script.PhaseAt(10).Phase);
        Assert.Equal(7, script.WaitAt(10)!.Value, 9);
        Assert.Null(script.WaitAt(18));
        Assert.Equal(AgentActivity.Tools, script.PhaseAt(40).Phase);
        Assert.Equal(120 + 90 - 53, script.IdleAt(90), 9);
        Assert.Null(script.CursorAt(58));
        Assert.Equal(1.5 + (0.55 - 1.5) * 0.5, script.CursorAt(59.8)!.Value.X, 9);
        Assert.Equal(92, script.Seconds);
        Assert.Null(script.SpeedAt(3));
        Assert.Null(CharacterSessionScript.Parse("[1, 2]"));
        Assert.Null(CharacterSessionScript.Parse("{"));
    }

    [Fact]
    public void TheSessionFeedsHerLiveInputsStepByStep()
    {
        var script = CharacterSessionScript.Parse("""
            {"hour": 2, "phases": [[0, "idle", 30], [5, "generating"]], "requests": [[5, 9]], "events": [[6, "newPeak"], [7, "trouble"]],
             "clicks": [8], "cursor": [[0, 0.5, 0], [10, 0, 0]], "speed": [[0, 0], [10, 1000]], "shots": [4, 1]}
            """)!;
        Assert.Equal([1.0, 4.0], script.Shots);
        Assert.Equal(500, script.SpeedAt(5)!.Value, 9);
        Assert.Equal(1000, script.SpeedAt(20)!.Value, 9);
        var live = new OrbCharacterLive();
        Assert.False(script.Apply(2, 1 / 60.0, 102, live));
        Assert.True(live.Scripted);
        Assert.Equal(AgentActivity.Idle, live.Phase);
        Assert.Equal(102 - 32, live.IdleSince, 9);
        Assert.Equal(2, live.Hour);
        Assert.Equal(0.4, live.ScriptedCursor!.Value.X, 9);
        Assert.False(script.Apply(6.5, 1 / 60.0, 106.5, live));
        Assert.Equal(AgentActivity.Generating, live.Phase);
        Assert.Equal(105, live.WaitingSince!.Value, 9);
        var peaks = live.Peaks;
        Assert.True(script.Apply(6, 1 / 60.0, 106, live));
        Assert.Equal(peaks + 1, live.Peaks);
        script.Apply(7, 1 / 60.0, 107, live);
        Assert.Equal(1, live.Troubles);
        script.Apply(8, 1 / 60.0, 108, live);
        Assert.Equal(1, live.Clicks);
        script.Apply(9.5, 1 / 60.0, 109.5, live);
        Assert.Null(live.WaitingSince);
    }
}
