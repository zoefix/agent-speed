using System.Windows;
using AgentSpeed.Core;
using AgentSpeed.Core.Perf;
using AgentSpeed.History.Perf;
using AgentSpeed.Perf;
using AgentSpeed.Presentation.History.Perf;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Windows.Tests;

/// <summary>
/// DESIGN 12, <i>Usage History: performance</i>, on the Windows side: the section loads and lays out in every layout and
/// state, its rows stay the same objects across updates (so values crossfade and meters spring), the sort is remembered,
/// a selection picks the chart's series, and the table's columns always fit the section.
/// </summary>
public class PerfPerformanceSectionTests
{
    private static readonly StringTable Table = Localizer.LoadEmbeddedTable();
    private static readonly DateOnly Today = new(2026, 10, 29);

    [Theory]
    [InlineData(920)]
    [InlineData(700)]
    [InlineData(420)]
    public void TheSectionLaysOutInEveryLayoutAndState(double width)
    {
        var heights = OnStaThread(() =>
        {
            using var model = new PerfHistoryViewModel(new Localizer(Table, LanguageCodes.ZhHant));
            var section = new PerfPerformanceSection { DataContext = model };
            var shown = new List<double>();
            foreach (var (records, first, source, analyzing) in States())
            {
                model.Update(records, first, Today, Today, source, analyzing);
                section.Measure(new Size(width, double.PositiveInfinity));
                section.Arrange(new Rect(0, 0, width, section.DesiredSize.Height));
                model.SetContentWidth(width);
                section.Measure(new Size(width, double.PositiveInfinity));
                section.Arrange(new Rect(0, 0, width, section.DesiredSize.Height));
                shown.Add(section.DesiredSize.Height);
            }

            return shown;
        });
        Assert.All(heights, h => Assert.True(h > 0));
    }

    [Fact]
    public void RowsStayTheSameObjectsAcrossUpdates()
    {
        using var model = new PerfHistoryViewModel(new Localizer(Table, LanguageCodes.En));
        var records = PerfCardSamples.Records(Today);
        model.Update(records, Today.AddDays(-29), Today, Today, null, analyzing: false);
        var first = model.Rows[0];
        model.Update(records, Today.AddDays(-6), Today, Today, null, analyzing: false);
        Assert.Contains(first, model.Rows);
        Assert.Equal(PerfHistoryState.Rows, model.State);
    }

    [Fact]
    public void TheSortIsRememberedAndMovesTheSameRows()
    {
        using var model = new PerfHistoryViewModel(new Localizer(Table, LanguageCodes.En));
        PerfSort? saved = null;
        model.RememberSort(null, sort => saved = sort);
        model.Update(PerfCardSamples.Records(Today), Today.AddDays(-29), Today, Today, null, analyzing: false);
        Assert.Equal(PerfSortColumn.Score, model.SortColumn);

        var rows = model.Rows.ToArray();
        model.SortBy(PerfSortColumn.Model);
        Assert.Equal(new PerfSort(PerfSortColumn.Model, false), saved);
        Assert.False(model.SortDescending);
        Assert.Equal(rows.OrderBy(r => r.ModelText, StringComparer.Ordinal), model.Rows);
        model.SortBy(PerfSortColumn.Model);
        Assert.True(model.SortDescending);
    }

    [Fact]
    public void TheMostTimedRowIsTheDefaultSeriesAndASelectionIsKept()
    {
        using var model = new PerfHistoryViewModel(new Localizer(Table, LanguageCodes.En));
        model.Update(PerfCardSamples.Records(Today), Today.AddDays(-29), Today, Today, null, analyzing: false);
        Assert.Equal("claude-opus-5-5", model.Selected!.Model);
        Assert.NotNull(model.Chart);

        var other = model.Rows.First(r => r.Model == "gpt-6.1-sol");
        model.Select(other);
        Assert.Same(other, model.Selected);
        model.Update(PerfCardSamples.Records(Today), Today.AddDays(-13), Today, Today, null, analyzing: false);
        Assert.Equal("gpt-6.1-sol", model.Selected!.Model);
        Assert.Contains("gpt-6.1-sol", model.TrendTitle, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWidthPicksTheLayout()
    {
        using var model = new PerfHistoryViewModel(new Localizer(Table, LanguageCodes.En));
        model.Update(PerfCardSamples.Records(Today), Today.AddDays(-29), Today, Today, null, analyzing: false);
        model.SetContentWidth(500);
        Assert.True(model.ShowsList);
        model.SetContentWidth(700);
        Assert.True(model.ShowsTable && model.ShowsParts);
        Assert.Contains(model.Headers, h => h.IsParts);
        model.SetContentWidth(900);
        Assert.DoesNotContain(model.Headers, h => h.IsParts);
        Assert.Equal(11, model.Headers.Count);
    }

    [Theory]
    [InlineData(640, false)]
    [InlineData(900, true)]
    [InlineData(380, false)]
    public void TableColumnsFillTheWidthWithoutScrollingSideways(double available, bool wide)
    {
        PerfColumnNeed[] needs =
        [
            new(56, 90, 52, 40), new(72, 44, 44, 44), new(28, 76, 50, 50), new(28, 60, 32, 32),
            new(28, 96, 64, 64), new(20, 54, 54, 54), new(34, 62, 30, 30),
        ];
        var widths = PerfTableColumns.Fit(available, 150, needs, wide);
        Assert.Equal(needs.Length + 1, widths.Length);
        Assert.True(widths.Sum() <= available + 0.5);
        for (var i = 0; i < needs.Length; i++)
        {
            Assert.True(widths[i + 1] >= needs[i].Content + 2 * PerfTableColumns.CellPadding - 0.5);
        }
    }

    private static IEnumerable<(IReadOnlyList<PerfRecord> Records, DateOnly First, SourceKind? Source, bool Analyzing)> States() =>
    [
        ([], Today.AddDays(-6), null, true),
        ([], Today.AddDays(-6), null, false),
        (PerfCardSamples.Records(Today), Today.AddDays(-29), null, false),
        (PerfCardSamples.Records(Today), Today, null, false),
        (PerfCardSamples.Records(Today), Today.AddDays(-13), SourceKind.Pi, false),
    ];

    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return error is null ? result : throw new InvalidOperationException("Layout failed.", error);
    }
}
