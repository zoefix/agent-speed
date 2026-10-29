using AgentSpeed.Core;
using AgentSpeed.Core.Perf;
using AgentSpeed.Core.Work;
using AgentSpeed.Presentation.Perf;
using AgentSpeed.Presentation.Tests.TestSupport;

namespace AgentSpeed.Presentation.Tests.Perf;

/// <summary>The score card (性能分析卡, DESIGN 12, <i>Score card</i>) built from the prototype's agents.</summary>
public sealed class PerfCardTests
{
    private static readonly Localization.Localizer Zh = PerfData.Loc();

    [Fact]
    public void NovaReadsLikeThePrototype()
    {
        var nova = PerfData.Nova();
        Assert.Equal(1213, nova.Shown);
        var card = PerfCard.Build(PerfData.Subject(nova), Zh);
        Assert.Equal("1,213", card.Number);
        Assert.Equal("claude-opus-5-5 · Claude Code", card.Meta);
        Assert.Equal(PerfNumberTone.Live, card.NumberTone);
        Assert.Equal(new PerfChip("比平常 +8%", PerfChipTone.Mint), card.Usual);
        Assert.Equal("標準 · 參考水準的 1.21 倍 · 最慢的一環：首段延遲 P50", card.Summary);
        Assert.Equal(string.Empty, card.ProvisionalHint);
        Assert.Equal(["速度", "效率"], card.Groups.Select(g => g.Title));
        Assert.Equal([PerfComponentId.T, PerfComponentId.L, PerfComponentId.R], card.Groups[0].Rows.Select(r => r.Id));
        Assert.Equal([PerfComponentId.N, PerfComponentId.U, PerfComponentId.S, PerfComponentId.H], card.Groups[1].Rows.Select(r => r.Id));
        var throughput = card.Groups[0].Rows[0];
        Assert.Equal(("77 tok/s", "1,540"), (throughput.Value, throughput.SubScore));
        var latency = card.Groups[0].Rows[1];
        Assert.Equal(("5.4 秒", "926"), (latency.Value, latency.SubScore));
        var thinking = card.Groups[0].Rows[2];
        Assert.False(thinking.Present);
        // METRICS 6.9: only DeepSeek Harness never records reasoning; Claude Code's responses without any are too few samples
        // (the prototype's card labels this row not recorded; the metrics and their reference implementation decide).
        Assert.Equal(("樣本不足", "–", 0.0), (thinking.Value, thinking.SubScore, thinking.Fill));
        Assert.Equal("資料不到 3 筆，暫不計入分數。", thinking.Meaning);
        Assert.EndsWith("· 共 42 次工具呼叫", card.Groups[1].Rows[0].Meaning, StringComparison.Ordinal);
        Assert.Equal(("292 毫秒", "1,370"), (card.Groups[1].Rows[1].Value, card.Groups[1].Rows[1].SubScore));
        Assert.Equal("有效計時請求 20 個 · 近 30 分鐘", card.Facts[0]);
        Assert.Equal(0.5 + Math.Log2(1.213) / 4, card.Fill, 3);
    }

    [Fact]
    public void ADescriptionFollowsTheDesignOrder()
    {
        var card = PerfCard.Build(PerfData.Subject(PerfData.Nova()) with { Work = new WorkSignals(null, null, 0.78, 2, 2) }, Zh);
        var description = card.Description;
        var parts = new[] { "性能 1,213，標準", "比平常高 8%", "最慢的一環", "輸出吞吐：77 tok/s，分數 1,540", "思考速度：樣本不足", "新動作 78%", "有效計時請求 20 個",
            "1000 是參考水準" };
        var at = 0;
        foreach (var part in parts)
        {
            var next = description.IndexOf(part, at, StringComparison.Ordinal);
            Assert.True(next >= at, $"'{part}' after {at} in {description}");
            at = next;
        }
    }

    [Fact]
    public void AProvisionalRestingHeroCard()
    {
        var nova = PerfData.Nova(PerfForm.Resting, n: 7, baseline: null);
        var card = PerfCard.Build(PerfData.Subject(nova) with { TopCount = 4 }, Zh);
        Assert.True(card.Provisional);
        Assert.Equal("閒置 · 4 個 agent 中最高", card.Eyebrow);
        Assert.Equal(PerfNumberTone.Resting, card.NumberTone);
        Assert.Equal(PerfChipTone.Outlined, card.Usual!.Tone);
        Assert.Equal("只有 7 個有效計時請求，分數還不穩定；滿 10 個後虛線就會消失。", card.ProvisionalHint);
    }

    [Fact]
    public void AVeryFastOpenCodeAgentLeavesOutWhatItDoesNotRecord()
    {
        var card = PerfCard.Build(PerfData.Subject(PerfData.Quasar, "quasar"), Zh);
        Assert.Equal("12,840", card.Number);
        Assert.Equal("極快 · 參考水準的 12.84 倍", card.Summary);
        var latency = card.Groups[0].Rows[1];
        Assert.Equal(("不記錄", false), (latency.Value, latency.Present));
        var cache = card.Groups[1].Rows[3];
        Assert.Equal("樣本不足", cache.Value);
        Assert.Equal("資料不到 3 筆，暫不計入分數。", cache.Meaning);
        Assert.Equal(1, card.Fill);
    }

    [Fact]
    public void NoScoreSaysWhyAndHasNoComponents()
    {
        var card = PerfCard.Build(PerfData.Subject(PerfData.Lumen, "lumen"), Zh);
        Assert.Equal("–", card.Number);
        Assert.Equal(PerfNumberTone.None, card.NumberTone);
        Assert.Null(card.Usual);
        Assert.Empty(card.Groups);
        Assert.Equal("近 30 分鐘只有 2 個有效計時請求；至少要 3 個才會評分。", card.Summary);
        Assert.Equal(0, card.Fill);
    }

    [Fact]
    public void EverySummaryForm()
    {
        var all = PerfData.Evaluation(SourceKind.Codex, PerfForm.Live, 20, 30, "gpt-6.1-sol",
            (92, 20), (3600, 20), (5.1, 30), (null, 0), (0.99, 20), (160, 30), (0.98, 30));
        Assert.EndsWith("每一項都高於參考", PerfCard.SummaryLine(all, all.Shown!.Value, Zh), StringComparison.Ordinal);
        var alone = PerfData.Quasar;
        Assert.Equal("極快 · 參考水準的 12.84 倍", PerfCard.SummaryLine(alone, alone.Shown!.Value, Zh));
    }

    [Fact]
    public void TheEnglishCard()
    {
        var card = PerfCard.Build(PerfData.Subject(PerfData.Nova()), PerfData.Loc(Localization.LanguageCodes.En));
        Assert.Equal("Standard · 1.21× the reference · slowest part: Latency P50", card.Summary);
        Assert.Equal("+8% vs usual", card.Usual!.Text);
    }
}
