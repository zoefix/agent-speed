using AgentSpeed.Presentation.Boards;
using AgentSpeed.Presentation.Boards.Demo;
using AgentSpeed.Presentation.Boards.Effects;
using AgentSpeed.Presentation.Boards.Gpu;
using AgentSpeed.Presentation.Boards.Scene;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;

namespace AgentSpeed.Presentation.Tests.Boards;

/// <summary>The scene's live metrics (DESIGN 2c, Live metrics): what the sub-gauge and the rows draw, and where.</summary>
public class BoardGaugeSceneTests
{
    private static readonly Localizer Zh = new(Localizer.LoadEmbeddedTable(), LanguageCodes.ZhHant);

    /// <summary>Every layer exists; each key's slot carries a unique u0 so a drawn instance names its layer.</summary>
    internal sealed class KeyedLayers : IBoardLayers
    {
        private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
        private readonly List<string> _keys = [];

        public bool TryGet(string key, out BoardLayerSlot slot)
        {
            if (!_ids.TryGetValue(key, out var id))
            {
                id = _keys.Count + 1;
                _ids[key] = id;
                _keys.Add(key);
            }

            var u = id / 100000f;
            if (key.StartsWith("#v", StringComparison.Ordinal) && key.Length > 3)
            {
                var set = (BoardValueSet)int.Parse(key[2..^1], System.Globalization.CultureInfo.InvariantCulture);
                var pad = BoardValueGlyphs.PadOf(set);
                slot = new BoardLayerSlot(u, 0, u, 0, 7 + 2 * pad, 20 + 2 * pad, Baseline: pad + 14);
            }
            else if (key.StartsWith("#g.", StringComparison.Ordinal))
            {
                // Labels about two CJK characters, ≥ narrow, units like tok/s.
                var text = key is "#g.ge" ? 5 : key.StartsWith("#g.l", StringComparison.Ordinal) ? 18 : 24;
                slot = new BoardLayerSlot(u, 0, u, 0, text + 2 * BoardGauge.TextPad, 16, Baseline: 12);
            }
            else
            {
                slot = new BoardLayerSlot(u, 0, u, 0, key.EndsWith("#v", StringComparison.Ordinal) ? 60 : 200, 30);
            }

            return true;
        }

        public string KeyOf(in BoardLayerInstance layer) => _keys[(int)Math.Round(layer.Uv.X * 100000) - 1];
    }

    private static (BoardScene Scene, KeyedLayers Layers, BoardDrawList List, BoardTick Tick, BoardDemoFeed Feed) Run(BoardStyle style, BoardSize size,
        BoardReadouts readouts, int ticks = 12, bool noReasoning = false)
    {
        var feed = new BoardDemoFeed(BoardSimScenario.Team) { Localizer = Zh, Readouts = readouts };
        feed.Sim.NoReasoning = noReasoning;
        var scene = new BoardScene(style, size, 3);
        scene.Configure(style, size, new BoardEffectsPolicy(ReduceMotion: true));
        BoardTick tick = null!;
        for (var i = 0; i < ticks; i++)
        {
            tick = feed.Step(style, size);
            scene.Apply(tick, i * 0.5);
        }

        scene.Settle();
        var layers = new KeyedLayers();
        var list = new BoardDrawList();
        scene.Render(100, layers, list);
        return (scene, layers, list, tick, feed);
    }

    private static List<BoardLayerInstance> Drawn(KeyedLayers layers, BoardDrawList list, Func<string, bool> key)
    {
        var found = new List<BoardLayerInstance>();
        foreach (var layer in list.Layers)
        {
            if (key(layers.KeyOf(layer)))
            {
                found.Add(layer);
            }
        }

        return found;
    }

    private static bool InSet(string key, BoardValueSet set) => key.StartsWith("#v" + (int)set, StringComparison.Ordinal) && key.Length ==
        ("#v" + (int)set).Length + 1;

    private static BoardGaugePlacement Placement(BoardScene scene, KeyedLayers layers)
    {
        var hidden = scene.Tick!.ThinkingHidden;
        return BoardGaugeLayout.Place(scene.Style, scene.Size, scene.Readouts, scene.Gauge.Cells(scene.Style, scene.Size, scene.Readouts, layers, 100),
            scene.Hero.Width, scene.Hero.Height, hidden ? 1 : 0, hidden ? 0 : 1);
    }

    [Theory]
    [InlineData(BoardStyle.Glass, BoardSize.M)]
    [InlineData(BoardStyle.Neon, BoardSize.L)]
    [InlineData(BoardStyle.Timing, BoardSize.S)]
    public void The性能NumberSitsOnItsBaselineInTheSubGauge(BoardStyle style, BoardSize size)
    {
        var (scene, layers, list, tick, _) = Run(style, size, BoardReadouts.All);
        var placement = Placement(scene, layers);
        var main = Drawn(layers, list, k => InSet(k, style == BoardStyle.Neon ? BoardValueSet.GaugeMainPlain : BoardValueSet.GaugeMain) ||
            InSet(k, BoardValueSet.GaugeMain));
        Assert.NotEmpty(main);
        var m = BoardCanvas.Margin;
        var expected = m + scene.Metrics.HeroTop + placement.MainBaseline;
        var pad = BoardValueGlyphs.PadOf(BoardValueSet.GaugeMain);
        Assert.Contains(main, l => Math.Abs(l.Rect.Y + pad + 14 - expected) < 1e-3 || Math.Abs(l.Rect.Y + 2 + 14 - expected) < 1e-3);
        // Right-aligned at the hero's side inset.
        Assert.Equal(scene.Hero.Width, placement.Left + placement.Width, 6);
        Assert.Equal(tick.Gauge.Intelligence, scene.Gauge.Readouts.Intelligence);
    }

    [Fact]
    public void GlassDrawsItsTrackValueArcAndTip()
    {
        var (scene, layers, list, tick, _) = Run(BoardStyle.Glass, BoardSize.M, BoardReadouts.All);
        var placement = Placement(scene, layers);
        var m = BoardCanvas.Margin;
        var cx = m + scene.Metrics.Pad + placement.CenterX;
        var arcs = list.Instances.ToArray().Where(i => (OrbShape)(int)i.A.W == OrbShape.BandArc && Math.Abs(i.A.X - cx) < 1e-3).ToArray();
        Assert.Equal(BoardGaugeSpec.For(BoardStyle.Glass, BoardSize.M).ArcRadius, arcs[0].B.X, 3);

        // The track, then the glow and at least one gradient piece; a provisional value is dotted instead.
        Assert.True(tick.Gauge.Score.HasScore);
        Assert.True(tick.Gauge.Score.IsProvisional ? arcs.Length == 1 : arcs.Length >= 3, $"{arcs.Length} arcs");
    }

    [Fact]
    public void NeonLightsItsMeterByTheScoreOnTheLogScale()
    {
        var (_, _, list, tick, _) = Run(BoardStyle.Neon, BoardSize.M, BoardReadouts.All);
        var instances = list.Instances.ToArray();
        var segments = instances.Where(i => (OrbShape)(int)i.A.W == OrbShape.RoundedRect && Math.Abs(i.B.Y - 1.5) < 1e-3).ToArray();

        // round(f × 12), f = clamp(log2(v / 250) / 4, 0, 1): 6 at 1000, all 12 from 4000.
        var lit = BoardHeroScore.Lit(tick.Gauge.Shown);
        Assert.Equal((int)Math.Round(Math.Clamp(Math.Log2(tick.Gauge.Shown!.Value / 250.0) / 4, 0, 1) * 12, MidpointRounding.AwayFromZero), lit);
        Assert.True(lit > 0);

        // Each segment once: lit (glowing on dark; hollow while provisional) over the first `lit`, unlit after them.
        var hollow = instances.Count(i => (OrbShape)(int)i.A.W == OrbShape.RoundedRing && Math.Abs(i.B.Y - (1.5 - Presentation.Perf.PerfProvisionalMark.HollowInset)) < 1e-3);
        Assert.Equal(tick.Gauge.Score.IsProvisional ? lit : 0, hollow);
        Assert.Equal(12, segments.Length + hollow);
        Assert.Equal(12 - lit, segments.Count(s => s.Color.W > 0.001 && s.Extra.Y <= 0.001));
    }

    [Fact]
    public void TimingDrawsItsDataBox()
    {
        var (scene, layers, list, _, _) = Run(BoardStyle.Timing, BoardSize.M, BoardReadouts.All);
        var placement = Placement(scene, layers);
        Assert.Equal(134, placement.Width, 6);
        var boxes = list.Instances.ToArray().Where(i => (OrbShape)(int)i.A.W is OrbShape.RoundedRect or OrbShape.RoundedRing &&
            Math.Abs(i.B.X - 67) < 0.6 && Math.Abs(i.B.Y - placement.Height / 2) < 0.6).ToArray();
        Assert.True(boxes.Length >= 2, "fill and inner stroke");
        Assert.Equal(58, placement.Height, 6);
    }

    [Theory]
    [InlineData(BoardStyle.Glass)]
    [InlineData(BoardStyle.Timing)]
    public void ThinkingHiddenAtZeroLeavesTheTable(BoardStyle style)
    {
        var (scene, layers, list, tick, _) = Run(style, BoardSize.M, BoardReadouts.All, ticks: 20, noReasoning: true);
        Assert.True(tick.ThinkingHidden);
        Assert.Empty(Drawn(layers, list, k => k == BoardGauge.KeyOf(BoardGaugePart.LabelThinking)));
        Assert.NotEmpty(Drawn(layers, list, k => k == BoardGauge.KeyOf(BoardGaugePart.LabelCost)));
        var spec = BoardGaugeSpec.For(style, BoardSize.M);
        var placement = Placement(scene, layers);
        Assert.Equal(BoardGaugeLayout.Height(style, spec, BoardReadouts.All, false), placement.Height, 6);
    }

    [Fact]
    public void RowsDrawThe性能CellOnTheValuesBaseline()
    {
        var (scene, layers, list, _, _) = Run(BoardStyle.Glass, BoardSize.M, BoardReadouts.All);
        var m = BoardCanvas.Margin;
        var right = m + scene.Metrics.ListLeft + scene.Metrics.ListWidth;
        var cells = Drawn(layers, list, k => InSet(k, BoardValueSet.IntelGroup) || InSet(k, BoardValueSet.IntelGroupRest));
        Assert.NotEmpty(cells);
        var pad = BoardValueGlyphs.PadOf(BoardValueSet.IntelGroup);
        Assert.All(cells, c => Assert.True(c.Rect.X + c.Rect.Z - pad <= right - scene.Columns.IntelligenceRight + 1e-3 ||
            c.Rect.X + c.Rect.Z - 2 <= right - scene.Columns.IntelligenceRight + 1e-3));
        Assert.NotEmpty(Drawn(layers, list, k => k == BoardScene.CaptionsKey));
    }

    // The second line carries only the detail (in the row's body): no value glyph sits on it, whatever the switches.
    [Theory]
    [InlineData(BoardStyle.Glass, true)]
    [InlineData(BoardStyle.Timing, true)]
    [InlineData(BoardStyle.Neon, true)]
    [InlineData(BoardStyle.Glass, false)]
    public void TwoLineRowsDrawNoValueOnTheSecondLine(BoardStyle style, bool all)
    {
        var (scene, layers, list, tick, _) = Run(style, BoardSize.M, all ? BoardReadouts.All : BoardReadouts.None);
        var m = BoardCanvas.Margin;
        var groups = tick.Entries.Where(e => e.Kind == BoardEntryKind.Group).ToList();
        Assert.NotEmpty(groups);
        Assert.All(groups, e => Assert.True(e.TwoLine));
        var baselines = new List<double>();
        foreach (var layer in list.Layers)
        {
            var key = layers.KeyOf(layer);
            if (key.StartsWith("#v", StringComparison.Ordinal) && key.Length > 3)
            {
                var set = (BoardValueSet)int.Parse(key[2..^1], System.Globalization.CultureInfo.InvariantCulture);
                baselines.Add(layer.Rect.Y + BoardValueGlyphs.PadOf(set) + 14);
            }
        }

        Assert.NotEmpty(baselines);
        foreach (var row in scene.Rows.Where(r => r.Entry.Kind == BoardEntryKind.Group))
        {
            // The figures leave line 1 for their shared baseline centred on the row, clear of line 2.
            var top = m + scene.Metrics.ListTop + row.Y;
            Assert.Contains(baselines, b => Math.Abs(b - (top + scene.FigureBaseline(row.Entry))) < 1e-3);
            Assert.DoesNotContain(baselines, b => Math.Abs(b - (top + BoardScene.FirstBaseline)) < 1e-3);
            Assert.DoesNotContain(baselines, b => Math.Abs(b - (top + BoardScene.SecondBaseline(style))) < 1.5);
        }
    }

    [Fact]
    public void SmallBoardsNeverDrawTheSecondLine()
    {
        var (_, _, _, tick, _) = Run(BoardStyle.Neon, BoardSize.S, BoardReadouts.All);
        Assert.DoesNotContain(tick.Entries, e => e.TwoLine);
    }

    [Theory]
    [InlineData(BoardStyle.Glass)]
    [InlineData(BoardStyle.Timing)]
    [InlineData(BoardStyle.Neon)]
    public void EveryReadoutOffDrawsNoLiveMetrics(BoardStyle style)
    {
        var (scene, layers, list, _, _) = Run(style, BoardSize.M, BoardReadouts.None);
        Assert.Equal(BoardMetrics.For(style, BoardSize.M), scene.Metrics);
        Assert.Empty(Drawn(layers, list, k => k.StartsWith("#g.", StringComparison.Ordinal) || k == BoardScene.CaptionsKey));
        Assert.Empty(Drawn(layers, list, k => InSet(k, BoardValueSet.GaugeMain) || InSet(k, BoardValueSet.IntelGroup) ||
            InSet(k, BoardValueSet.IntelGroupRest)));
    }

    [Fact]
    public void ReadoutChangesRollWithTheHeroAndASwitchCrossfadesTheHero()
    {
        var feed = new BoardDemoFeed(BoardSimScenario.Team) { Localizer = Zh, Readouts = BoardReadouts.All };
        var scene = new BoardScene(BoardStyle.Glass, BoardSize.M, 3);
        scene.Configure(BoardStyle.Glass, BoardSize.M, new BoardEffectsPolicy(Settings.EffectsIntensity.Off));
        var t = 0.0;
        BoardTick previous = feed.Step(BoardStyle.Glass, BoardSize.M);
        scene.Apply(previous, t);
        scene.Settle();
        var rolled = false;
        for (var i = 0; i < 30 && !rolled; i++)
        {
            t += 0.5;
            var tick = feed.Step(BoardStyle.Glass, BoardSize.M);
            scene.Apply(tick, t);
            rolled = tick.Gauge.Thinking != previous.Gauge.Thinking && scene.Gauge.Animating(t + 0.1) && !scene.Gauge.Animating(t + 0.3);
            previous = tick;
        }

        Assert.True(rolled, "a readout change animates for at most 0.24 s");

        // Switching 思考 off crossfades the hero in 0.2 s and needs 60 fps meanwhile.
        feed.Readouts = BoardReadouts.All with { Thinking = false };
        t += 0.5;
        scene.Apply(feed.Step(BoardStyle.Glass, BoardSize.M), t);
        Assert.InRange(scene.SwitchAlpha(t + 0.05), 0.01, 0.99);
        Assert.Equal(FrameClass.Fps60, scene.Demand(t + 0.05));
        Assert.Equal(1, scene.SwitchAlpha(t + 0.25), 6);
    }
}
