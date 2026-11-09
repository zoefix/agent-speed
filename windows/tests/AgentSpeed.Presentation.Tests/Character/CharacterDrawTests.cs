using System.Diagnostics;
using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.Character;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Orb.Gpu.Styles;
using AgentSpeed.Presentation.Orb.Lines;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Tests.Gpu;
using Xunit.Abstractions;

namespace AgentSpeed.Presentation.Tests.Character;

/// <summary>The <c>character</c> style's draw list (DESIGN 2a.6): layers in order, the empty state, frame classes, cost per frame.</summary>
public class CharacterDrawTests(ITestOutputHelper output)
{
    private const double Redline = 2000;

    private static readonly OrbReadoutStrings Strings = new("tok/s", "NEW PEAK",
        Enum.GetValues<SpeedTitle>().Select(t => t.ToString().ToUpperInvariant()).ToArray(), true);

    private static readonly Localizer En = new(Localizer.LoadEmbeddedTable(), LanguageCodes.En);

    internal static (OrbGpuOrb Orb, FakeGlyphs Glyphs) Create(CharacterPack? pack, double size = 148, bool dark = true,
        EffectsIntensity level = EffectsIntensity.Standard, bool reduceMotion = false)
    {
        var glyphs = new FakeGlyphs();
        var orb = new OrbGpuOrb(7, glyphs);
        var effects = new EffectsPolicy(level, reduceMotion);
        var d = OrbStyles.FootprintDiameter(OrbStyle.Character, size);
        var context = new OrbGpuContext(d, 2, OrbTokens.For(dark ? OrbAppearance.Dark : OrbAppearance.Light), effects);
        orb.SetCharacter(new OrbCharacterContent
        {
            Pack = pack,
            NoneInstalled = pack is null,
            EmptyLabel = "Download a character",
            Readouts = Settings.Orbs.CharacterReadouts.Default with { Model = true },
            ModelName = "Claude Opus 4.5",
            CostGlyph = "Cost",
            TodayLine = "Today 1.2M",
            AgentsLine = "3 agents",
        });
        orb.Configure(new OrbGpuConfig(OrbStyle.Character, d, effects, true, true, Redline, Strings, null, SourceSet.Empty), context);
        return (orb, glyphs);
    }

    internal static void Feed(OrbGpuOrb orb, double time, double value)
    {
        orb.SetData(time, value, HeatMath.RawTier(value, Redline), SpeedTitles.Raw(value, Redline), value > 0 ? [SourceKind.ClaudeCode] : [], "info", null);
        var rx = value * 52;
        orb.SetCharacterNetwork(time, OrbLinesText.Traffic(new TrafficRate(rx, rx / 4), false, En));
        orb.SetCharacterReadings(time, null, value > 0 ? new Cost.LiveMoney("0.0042", "US$/s", "$", false, false, 4200, "", "") : null, value > 0);
    }

    private static CharacterPack TinyPack() =>
        CharacterPack.Load(Character.TinyPack.Make(Character.TinyPack.Bands(("idle", Character.TinyPack.RigJson()), ("boost", Character.TinyPack.RigJson()))));

    [Fact]
    public void WithoutACharacterTheRingAndTheEmptyLabelShow()
    {
        var (orb, glyphs) = Create(null);
        Feed(orb, 10, 300);
        orb.SnapNext();
        orb.Render(10);
        Assert.Equal(0, orb.List.CharacterMeshCount);
        Assert.Contains("Download a character", glyphs.Texts);
        Assert.True(orb.List.Count > 96);
    }

    [Fact]
    public void TheCharacterDrawsBetweenHerLightsWithTheCardsInFront()
    {
        var (orb, glyphs) = Create(TinyPack());
        var time = 10.0;
        orb.SnapNext();
        for (var n = 0; n < 240; n++)
        {
            if (n % 30 == 0)
            {
                Feed(orb, time, 600);
            }

            orb.Render(time);
            time += 1 / 60.0;
        }

        var batches = orb.List.Batches.ToArray();
        var mesh = Array.FindIndex(batches, b => b.Pass == OrbPass.CharacterMesh);
        Assert.True(mesh > 0, "the mesh is drawn");
        Assert.Contains(batches[..mesh], b => b.Pass == OrbPass.CharacterDots);
        Assert.Contains(batches[(mesh + 1)..], b => b.Pass == OrbPass.CharacterDots);
        var firstText = Array.FindIndex(batches, b => b.Pass == OrbPass.Text);
        Assert.True(firstText > mesh, "every text is in front of her");
        Assert.Contains("NEW PEAK", Strings.Peak);
        Assert.Contains("Claude Opus 4.5", glyphs.Texts);
        Assert.Contains("tok/s", glyphs.Texts);
        Assert.Equal(FrameClass.Fps60, orb.Demand);
    }

    [Fact]
    public void ReduceMotionKeepsTheCardsStill()
    {
        var (orb, _) = Create(TinyPack(), reduceMotion: true);
        var time = 10.0;
        orb.SnapNext();
        Feed(orb, time, 0);
        for (var n = 0; n < 600; n++)
        {
            orb.Render(time);
            time += 1 / 60.0;
        }

        var draw = Assert.IsType<CharacterDraw>(orb.Style);
        Assert.All(draw.Wings.Places, p => Assert.Equal(0, p.Spin));
        Assert.Equal(FrameClass.None, orb.Demand);
    }

    /// <summary><c>AGENT_SPEED_CHARACTER_PACK</c>: a real pack's frame (step, trace, cards, draw list) stays within the CPU budget.</summary>
    [Fact]
    public void ARealPackBuildsAFrameQuickly()
    {
        if (Environment.GetEnvironmentVariable("AGENT_SPEED_CHARACTER_PACK") is not { Length: > 0 } path)
        {
            return;
        }

        var (orb, _) = Create(CharacterPack.Load(path), 240);
        var time = 10.0;
        orb.SnapNext();
        for (var n = 0; n < 300; n++)
        {
            if (n % 30 == 0)
            {
                Feed(orb, time, 1500);
            }

            orb.Render(time);
            time += 1 / 60.0;
        }

        var watch = Stopwatch.StartNew();
        var before = GC.GetAllocatedBytesForCurrentThread();
        const int frames = 600;
        for (var n = 0; n < frames; n++)
        {
            if (n % 30 == 0)
            {
                Feed(orb, time, 1500 + n);
            }

            orb.Render(time);
            time += 1 / 60.0;
        }

        var ms = watch.Elapsed.TotalMilliseconds / frames;
        var bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / frames;
        output.WriteLine($"character frame: {ms:0.000} ms, {bytes} B allocated, {orb.List.Count} shapes, {orb.List.TextCount} texts, {orb.List.CharacterDotCount} dots");
        Assert.True(ms < 2, $"{ms:0.000} ms per frame");
    }
}
