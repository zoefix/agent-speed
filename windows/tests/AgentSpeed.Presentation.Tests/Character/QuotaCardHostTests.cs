using AgentSpeed.Core;
using AgentSpeed.Core.Quota;
using AgentSpeed.Presentation.Character;
using AgentSpeed.Presentation.Characters;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Orb.Gpu.Styles;
using AgentSpeed.Presentation.Quota;
using AgentSpeed.Presentation.Tests.Gpu;
using AgentSpeed.Presentation.Titles;

namespace AgentSpeed.Presentation.Tests.Character;

/// <summary>Host sources in the 額度 card of a character orb (DESIGN 13 <i>Host quotas</i>, 2a.6 額度).</summary>
public sealed class QuotaCardHostTests
{
    private const long Hour = 3_600_000;
    private const long Now = 1794338773000;
    private const string Devbox = "A0000000-0000-4000-8000-000000000001";
    private const string Gpu = "A0000000-0000-4000-8000-000000000002";
    private static readonly Localizer En = new(Localizer.LoadEmbeddedTable(), LanguageCodes.En);

    private static QuotaSnapshot Snapshot(SourceKind source, int percent) =>
        new(source, Now - 60_000, [QuotaWindow.Of(QuotaWindowKind.SevenDay, percent, Now + 50 * Hour)]);

    private static OrbQuotaSource Source(SourceKind source, double bar, string? host = null, string? hostId = null) =>
        new(source, source.ToString(), Rgba.White, [new OrbQuotaWindow("7d", (int)(bar * 100), bar, QuotaTone.Normal, "Fri", false)], host, hostId);

    [Fact]
    public void AHostItemsHeaderReadsSourceDotHostAndIsSpokenSo()
    {
        var card = OrbQuotaCard.Of(
            [new QuotaCardItem(Snapshot(SourceKind.Codex, 23)), new QuotaCardItem(Snapshot(SourceKind.Codex, 40), Devbox, "devbox")], Now, En,
            TimeZoneInfo.Utc);
        Assert.Equal(["Codex", "Codex"], card.Sources.Select(s => s.Name));
        Assert.Null(card.Sources[0].HostTail);
        Assert.Equal(" · devbox", card.Sources[1].HostTail);
        Assert.Equal("devbox", card.Sources[1].Host);
        Assert.NotEqual(card.Sources[0].Key, card.Sources[1].Key);
        Assert.StartsWith("Quota: Codex 7d 23% used, ", card.Spoken, StringComparison.Ordinal);
        Assert.Contains(", Codex · devbox 7d 40% used, ", card.Spoken, StringComparison.Ordinal);
        Assert.NotEqual(card, card with { Sources = [card.Sources[0], card.Sources[1] with { Host = "devbox (2)" }] });
        Assert.Equal(" · devbox (2)", (card.Sources[1] with { Host = "devbox (2)" }).HostTail);
    }

    [Fact]
    public void TheSameSourceOnTwoHostsMovesOnItsOwn()
    {
        var motion = new OrbQuotaMotion();
        motion.Sync([Source(SourceKind.Codex, 0.2), Source(SourceKind.Codex, 0.5, "devbox", Devbox)], 0, reduceMotion: false, atOnce: true);
        Assert.Equal(2, motion.Count);

        motion.Sync([Source(SourceKind.Codex, 0.2), Source(SourceKind.Codex, 0.8, "devbox", Devbox)], 1, false, false);
        Assert.Equal(0.2, motion.Sources[0].Windows[0].Bar(1.2), 9);
        Assert.InRange(motion.Sources[1].Windows[0].Bar(1.2), 0.55, 0.8);
        Assert.Equal(1.0, motion.Sources[0].Windows[0].PercentAlpha(1.1), 9);

        motion.Sync([Source(SourceKind.Codex, 0.2)], 2, false, false);
        Assert.False(motion.Sources[0].Leaving);
        Assert.True(motion.Sources[1].Leaving);
        motion.Sync([Source(SourceKind.Codex, 0.2)], 3, false, false);
        Assert.Null(Assert.Single(motion.Sources).Data.HostId);
    }

    [Fact]
    public void AJoiningHostSourceTakesItsPlaceInTheCardsOrder()
    {
        var motion = new OrbQuotaMotion();
        var local = Source(SourceKind.Kimi, 0.2);
        var gpu = Source(SourceKind.Gemini, 0.4, "gpu-01.lab", Gpu);
        motion.Sync([local, gpu], 0, false, atOnce: true);
        motion.Sync([local, Source(SourceKind.Codex, 0.3, "devbox", Devbox), gpu], 1, false, false);
        Assert.Equal(["Kimi", "Codex@" + Devbox, "Gemini@" + Gpu], motion.Sources.Select(s => s.Data.Key));
        Assert.InRange(motion.Sources[1].Alpha(1.1), 0.3, 0.5);
    }

    // MARK: Drawn

    private static CharacterPack Pack() =>
        CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", TinyPack.RigJson()), ("boost", TinyPack.RigJson()))));

    private static (OrbGpuOrb Orb, FakeGlyphs Glyphs) Run(double size, OrbQuotaCard card, double frames = 120)
    {
        var (orb, glyphs) = CharacterDrawTests.Create(Pack(), size);
        orb.SetCharacter(orb.Character! with { Quota = card });
        var time = 10.0;
        orb.SnapNext();
        for (var n = 0; n < frames; n++, time += 1 / 60.0)
        {
            if (n % 30 == 0)
            {
                orb.SetData(time, 30, HeatMath.RawTier(30, 2000), SpeedTitles.Raw(30, 2000), [SourceKind.ClaudeCode], "info", null);
            }

            orb.Render(time);
        }

        return (orb, glyphs);
    }

    private static OrbQuotaCard Card(bool hosts) => OrbQuotaCard.Of(
        [
            new QuotaCardItem(Snapshot(SourceKind.Codex, 23)),
            hosts ? new QuotaCardItem(Snapshot(SourceKind.ClaudeDesktop, 40), Gpu, "gpu-01.lab") : new QuotaCardItem(Snapshot(SourceKind.ClaudeDesktop, 40)),
        ], Now, En, TimeZoneInfo.Utc);

    [Fact]
    public void AHostThatDoesNotFitAfterTheNameTakesItsOwnLineAndNeverWidensTheCard()
    {
        var (plain, _) = Run(148, Card(hosts: false));
        var (hosted, glyphs) = Run(148, Card(hosts: true));
        var a = Assert.IsType<CharacterDraw>(plain.Style).Wings.Sizes[(int)HudCard.Quota];
        var b = Assert.IsType<CharacterDraw>(hosted.Style).Wings.Sizes[(int)HudCard.Quota];
        Assert.Equal(a.W, b.W, 3);
        Assert.True(b.H > a.H + 1, $"{b.H} with the host's own line, {a.H} without");
        Assert.Contains("gpu-01.lab", glyphs.Texts);
    }

    [Fact]
    public void AHostThatFitsFollowsTheName()
    {
        var card = OrbQuotaCard.Of(
            [new QuotaCardItem(Snapshot(SourceKind.ClaudeDesktop, 23)), new QuotaCardItem(Snapshot(SourceKind.Codex, 40), Devbox, "box")], Now, En,
            TimeZoneInfo.Utc);
        var (orb, glyphs) = Run(148, card);
        var draw = Assert.IsType<CharacterDraw>(orb.Style);
        Assert.Contains(" · box", glyphs.Texts);
        Assert.DoesNotContain("box", glyphs.Texts);
        Assert.Equal(2, draw.QuotaLines.Count);
    }

    [Fact]
    public void AWarmFrameWithHostSourcesAllocatesNoMoreThanWithout()
    {
        static long Allocated(OrbQuotaCard card)
        {
            var (orb, _) = Run(148, card);
            var time = 20.0;
            for (var n = 0; n < 60; n++, time += 1 / 60.0)
            {
                orb.Render(time);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var n = 0; n < 240; n++, time += 1 / 60.0)
            {
                orb.Render(time);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        var without = Allocated(OrbQuotaCard.Empty);
        var with = Allocated(Card(hosts: true));
        Assert.True(with - without <= 240 * 64, $"{with} B with host sources, {without} B without over 240 frames");
    }
}
