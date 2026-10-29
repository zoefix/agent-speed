using AgentSpeed.Core;
using AgentSpeed.Core.Work;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.TaskScore;
using static AgentSpeed.Presentation.Tests.TestSupport.TaskData;

namespace AgentSpeed.Presentation.Tests;

/// <summary>The score card (DESIGN 12, <i>Score card</i>): every state, the predictions, the signals and the accessible text.</summary>
public class TaskScoreCardTests
{
    [Fact]
    public void AnUnverifiedCardShowsWhatWouldProveIt()
    {
        var card = TaskScoreCard.Build(Subject(Unverified), Loc());
        Assert.Equal("nova", card.Project);
        Assert.Equal("claude-sonnet-5-5 · Claude Code", card.Meta);
        Assert.Equal(new TaskChip(TaskState.Unverified, "尚未驗證", TaskChipTone.Neutral, TaskChipGlyph.DotRing), card.Chip);
        Assert.Equal("9", card.Number);
        Assert.Equal(TaskNumberTone.Live, card.NumberTone);
        Assert.True(card.HasProofMark);
        Assert.False(card.HasEyebrow);
        Assert.Equal("已修改，但還沒有執行測試、建置或檢查", card.StatusText);
        Assert.Equal([new TaskCompositionSegment(TaskDimension.Efficiency, 0, 9, false)], card.Composition);

        // No verification result yet: never a fake 0 / 40; E keeps its value.
        foreach (var row in card.Dimensions.Where(r => r.Dimension != TaskDimension.Efficiency))
        {
            Assert.Equal(TaskDimensionValueKind.Unverified, row.Kind);
            Assert.Equal("尚未驗證", row.ValueText);
            Assert.True(row.DottedTrack);
            Assert.Equal(0, row.Fill);
            Assert.Equal(string.Empty, row.MaxText);
        }

        var e = card.Dimensions.Single(r => r.Dimension == TaskDimension.Efficiency);
        Assert.Equal((TaskDimensionValueKind.Value, "9", " / 10", 0.9, false), (e.Kind, e.ValueText, e.MaxText, e.Fill, e.DottedTrack));
        Assert.Equal("行為效率 9 分，滿分 10", e.Spoken);
        Assert.Equal("驗證成功，尚未驗證", card.Dimensions[0].Spoken);
    }

    [Fact]
    public void DimensionRowsCarryTheirLabelsMaximaAndMeanings()
    {
        var card = TaskScoreCard.Build(Subject(Passing), Loc());
        Assert.Equal(["驗證成功", "錯誤收斂", "錯誤恢復", "行為效率", "驗證完整性"], card.Dimensions.Select(r => r.Label));
        Assert.Equal([40, 25, 15, 10, 10], card.Dimensions.Select(r => r.Max));
        Assert.Equal(["40", "20", "11", "7", "8"], card.Dimensions.Select(r => r.ValueText));
        Assert.Equal(86, card.Dimensions.Sum(r => r.Points));
        Assert.Equal("修改通過可靠的檢查：測試、建置或檢查零失敗", card.Dimensions[0].Description);
        Assert.All(card.Dimensions, r => Assert.Equal((double)r.Points / r.Max, r.Fill, 9));
        Assert.False(card.HasProofMark);
        Assert.Equal("評分構成", card.DimensionsTitle);
    }

    [Fact]
    public void APendingCardHoldsTheCreditARerunRestores()
    {
        var card = TaskScoreCard.Build(Subject(Pending, "quasar", SourceKind.Codex, "gpt-6.1-terra"), Loc());
        Assert.True(card.HasProofMark);
        Assert.Equal(TaskChipGlyph.Clock, card.Chip!.Glyph);
        var v = card.Dimensions[0];
        Assert.Equal((TaskDimensionValueKind.Pending, "待重新驗證", 0.0, 1.0, false), (v.Kind, v.ValueText, v.Fill, v.Ghost, v.DottedTrack));
        Assert.Equal("驗證成功，待重新驗證", v.Spoken);
        var k = card.Dimensions[4];
        Assert.Equal((TaskDimensionValueKind.Value, "0"), (k.Kind, k.ValueText));
        Assert.Equal(
            [
                new TaskCompositionSegment(TaskDimension.Verify, 0, 40, true),
                new TaskCompositionSegment(TaskDimension.Converge, 40, 18, false),
                new TaskCompositionSegment(TaskDimension.Recover, 58, 10, false),
                new TaskCompositionSegment(TaskDimension.Efficiency, 68, 6, false),
            ],
            card.Composition);

        var buildOnly = TaskScoreCard.Build(Subject(Pending with { VHeld = 20 }), Loc());
        Assert.Equal(0.5, buildOnly.Dimensions[0].Ghost);
        Assert.Equal(20, buildOnly.Composition[0].Length);
    }

    [Fact]
    public void AFailingCardPredictsTheFixAndListsItsBehaviour()
    {
        var subject = Subject(Failing, "rocket", SourceKind.Codex, "gpt-6.1-sol") with
        {
            Work = new WorkSignals(4, null, 0.46, 5, 1),
            FactLines = ["推理強度：xhigh", "加速：Fast"],
        };
        var card = TaskScoreCard.Build(subject, Loc());
        Assert.Equal(TaskChipTone.Amber, card.Chip!.Tone);
        Assert.Equal("未通過", card.Chip.Text);
        Assert.False(card.HasProofMark);
        Assert.Equal(
            [
                (TaskPredictionKind.Success, "38%", "成功", "依 54 個任務", true),
                (TaskPredictionKind.Stall, "41%", "卡住", "依 54 個任務", true),
                (TaskPredictionKind.FixFails, "52%", "修復失敗", "預設值 · 23/40", false),
            ],
            card.Predictions.Select(p => (p.Kind, p.Percent, p.Label, p.Flag, p.Calibrated)));
        Assert.Equal("修復失敗的機率 52%，預設值", card.Predictions[2].Spoken);
        Assert.Equal("成功的機率 38%，依 54 個任務校準", card.Predictions[0].Spoken);
        Assert.Equal("依你在 Codex 的任務紀錄校準；完成的任務不到 40 個，或幾乎都是同一種結果時，使用預設值。", card.PredictionNote);
        Assert.Equal(
            [
                (TaskSignalGlyph.Loop, "原地打轉：同一組動作已重複 4 次，且有失敗", true),
                (TaskSignalGlyph.Oscillation, "來回修改 2 次", false),
                (TaskSignalGlyph.Summary, "新動作 46% · 失敗 5 · 已修正 1", false),
            ],
            card.Signals.Select(s => (s.Glyph, s.Text, s.IsWarning)));
        Assert.Equal(["推理強度：xhigh", "加速：Fast"], card.Facts);
    }

    [Fact]
    public void FixFailureIsPredictedOnlyWhileFailing()
    {
        var passing = Passing with { Predictions = Passing.Predictions! with { FixFails = Prior(0.3, 10) } };
        Assert.Equal(2, TaskScoreCard.Build(Subject(passing), Loc()).Predictions.Count);
    }

    [Fact]
    public void APriorWithEnoughTasksSaysOnlyThatItIsAPrior()
    {
        var facts = Passing with { Predictions = new TaskPredictions(Prior(0.97, 212), Prior(0.05, 3)) };
        var card = TaskScoreCard.Build(Subject(facts), Loc());
        Assert.Equal(["預設值", "預設值 · 3/40"], card.Predictions.Select(p => p.Flag));
        Assert.Equal(["Prior", "Prior · 3/40"], TaskScoreCard.Build(Subject(facts), Loc(LanguageCodes.En)).Predictions.Select(p => p.Flag));
    }

    [Fact]
    public void ARestingCardIsPaleSaysIdleAndPredictsNothing()
    {
        var card = TaskScoreCard.Build(Subject(Resting, "orbit", SourceKind.ClaudeDesktop, "claude-fable-5-1"), Loc());
        Assert.Equal(TaskNumberTone.Resting, card.NumberTone);
        Assert.Equal("閒置", card.Eyebrow);
        Assert.Equal("72", card.Number);
        Assert.False(card.HasPredictions);
        Assert.Equal(string.Empty, card.PredictionNote);
        Assert.StartsWith("智力 72，閒置中，已通過", card.FullDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHerosCardNamesTheHighestOfNAgents()
    {
        var loc = Loc();
        var hero = TaskScoreCard.Build(Subject(Passing) with { TopCount = 4 }, loc);
        Assert.Equal("4 個 agent 中最高", hero.Eyebrow);
        Assert.StartsWith("智力 86，4 個 agent 中最高，已通過", hero.FullDescription, StringComparison.Ordinal);
        Assert.False(TaskScoreCard.Build(Subject(Passing) with { TopCount = 1 }, loc).HasEyebrow);
        Assert.Equal("閒置 · 2 個 agent 中最高", TaskScoreCard.Build(Subject(Resting) with { TopCount = 2 }, loc).Eyebrow);
    }

    [Fact]
    public void ACardWithoutAScoreShowsTheDashAndNoComposition()
    {
        var none = new TaskScoreFacts(TaskForm.None, TaskState.None, null, new TaskDimensions(0, 0, 0, 1.25, 0));
        var card = TaskScoreCard.Build(Subject(none, "lumen", SourceKind.Omp, "claude-opus-5-5"), Loc());
        Assert.Equal("–", card.Number);
        Assert.Null(card.Shown);
        Assert.Equal(TaskNumberTone.None, card.NumberTone);
        Assert.False(card.HasProofMark);
        Assert.Empty(card.Composition);
        Assert.Equal("尚無評分", card.Chip!.Text);
        Assert.Equal(TaskChipGlyph.None, card.Chip.Glyph);
        Assert.Equal("還沒有修改，也還沒有執行測試或建置", card.StatusText);
        var e = card.Dimensions[3];
        Assert.Equal((TaskDimensionValueKind.Absent, "–", true), (e.Kind, e.ValueText, e.DottedTrack));
        Assert.Equal("行為效率：尚無評分", e.Spoken);
        Assert.All(card.Dimensions.Take(3), r => Assert.Equal(TaskDimensionValueKind.Unverified, r.Kind));
        Assert.StartsWith("智力：尚無評分，還沒有修改", card.FullDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnsupportedSourceShowsTheHeaderTheReasonAndTheFacts()
    {
        var subject = new TaskCardSubject(SourceKind.DeepSeek, "tihu", "deepseek-v4-pro", TaskScoreFacts.None, ["今天的網路流量：1.2 MB"]);
        var card = TaskScoreCard.Build(subject, Loc());
        Assert.True(card.IsUnsupported);
        Assert.Null(card.Chip);
        Assert.Equal("這個來源不會記錄工具呼叫，所以無法為任務評分。", card.UnsupportedText);
        Assert.Empty(card.Dimensions);
        Assert.Equal(["今天的網路流量：1.2 MB"], card.Facts);
        Assert.Equal("這個來源不會記錄工具呼叫，所以無法為任務評分。，今天的網路流量：1.2 MB", card.FullDescription);
        Assert.False(TaskScoreCard.Build(Subject(Passing, source: SourceKind.Droid), Loc()).IsUnsupported);
        Assert.True(TaskScoreCard.Build(Subject(Passing, source: SourceKind.Trae), Loc()).IsUnsupported);
    }

    [Fact]
    public void AGroupRowsMetaCountsItsSubAgents()
    {
        var card = TaskScoreCard.Build(Subject(Failing, "rocket", SourceKind.Codex, "gpt-6.1-sol") with { SubAgents = 2 }, Loc());
        Assert.Equal("gpt-6.1-sol · Codex · 2 個子 agent", card.Meta);
        Assert.Equal("Codex", TaskScoreCard.Build(Subject(Failing, "rocket", SourceKind.Codex, null), Loc()).Meta);
    }

    [Fact]
    public void TheFullDescriptionFollowsDesignOrder()
    {
        var subject = Subject(Failing, "rocket", SourceKind.Codex) with { Work = new WorkSignals(null, 3, null, 0, 0), FactLines = ["推理強度：xhigh"] };
        var card = TaskScoreCard.Build(subject, Loc());
        var parts = card.FullDescription.Split('，');
        var text = card.FullDescription;
        int At(string piece)
        {
            var index = text.IndexOf(piece, StringComparison.Ordinal);
            Assert.True(index >= 0, piece);
            return index;
        }

        var order = new[]
        {
            At("智力 41，未通過"), At("cargo test：還有 3 個錯誤"), At("驗證成功 0 分，滿分 40"), At("修改通過可靠的檢查"), At("錯誤收斂 17 分，滿分 25"),
            At("驗證完整性 8 分，滿分 10"), At("成功的機率 38%"), At("卡住的機率 41%"), At("修復失敗的機率 52%"), At("重複同一組動作 3 次"),
            At("來回修改 2 次"), At("推理強度：xhigh"), At("權重是尚未校準的初始值"),
        };
        Assert.Equal(order.OrderBy(i => i), order);
        Assert.True(parts.Length > 10);
    }

    [Fact]
    public void TheStateIsSpokenOnce()
    {
        // DESIGN 12: unproven score words already say the state, so the chip's text is not added again.
        var card = TaskScoreCard.Build(Subject(Unverified), Loc());
        Assert.StartsWith("智力 9，尚未驗證，已修改", card.FullDescription, StringComparison.Ordinal);
        var pending = TaskScoreCard.Build(Subject(Pending), Loc(LanguageCodes.En));
        Assert.StartsWith("intelligence 34, re-check pending, pytest", pending.FullDescription, StringComparison.Ordinal);
        Assert.StartsWith("智力 86，已通過，", TaskScoreCard.Build(Subject(Passing), Loc()).FullDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCardIsWordedInEveryLanguage()
    {
        TaskScoreFacts[] states = [TaskScoreFacts.None, Unverified, Failing, Passing, Pending, Resting];
        foreach (var language in LanguageCodes.All)
        {
            var loc = Loc(language);
            foreach (var facts in states)
            {
                var card = TaskScoreCard.Build(Subject(facts) with { Work = new WorkSignals(3, null, 0.5, 2, 1), TopCount = 3 }, loc);
                string[] texts =
                [
                    card.Meta, card.Chip!.Text, card.StatusText, card.DimensionsTitle, card.PredictionsTitle, card.PredictionNote, card.SignalsTitle,
                    card.Footnote, card.FullDescription, card.Eyebrow ?? string.Empty,
                    .. card.Dimensions.SelectMany(r => new[] { r.Label, r.ValueText, r.Description, r.Spoken }),
                    .. card.Predictions.SelectMany(p => new[] { p.Label, p.Flag, p.Spoken }),
                    .. card.Signals.Select(s => s.Text),
                ];
                foreach (var text in texts)
                {
                    Assert.DoesNotContain("task.", text, StringComparison.Ordinal);
                    Assert.DoesNotContain("a11y.", text, StringComparison.Ordinal);
                    Assert.DoesNotContain("%@", text, StringComparison.Ordinal);
                    Assert.DoesNotContain("%d", text, StringComparison.Ordinal);
                }
            }
        }
    }
}
