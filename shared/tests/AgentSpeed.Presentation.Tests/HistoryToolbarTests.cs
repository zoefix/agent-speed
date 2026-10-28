using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.Tests;

public class HistoryToolbarTests
{
    /// <summary>The toolbar of a 720 DIP wide window: 16 DIP of window frame and 24 DIP of padding on each side.</summary>
    private const double MinimumToolbar = 720 - 16 - 2 * 24;

    /// <summary>The toolbar of the default 920 DIP wide window.</summary>
    private const double DefaultToolbar = 920 - 16 - 2 * 24;

    /// <summary>The export button with its icon only: a 14 DIP glyph, 11 + 12 DIP padding and a 1 DIP border.</summary>
    private const double ExportIcon = 40;

    /// <summary>
    /// The range labels (今天 … 自訂…) and the export label of each language at the window's font sizes (12 and 14 DIP),
    /// measured from Segoe UI's advance widths (segoeui.ttf) and widened by 8 % for the semibold selected item and Segoe UI
    /// Variable; Chinese and Japanese characters count a full em (they come from the CJK UI fonts).
    /// </summary>
    public static TheoryData<string, double[], double> Labels => new()
    {
        { LanguageCodes.ZhHant, [25.9, 25.9, 23.5, 30.5, 30.5, 25.9, 25.9, 35.4], 72.2 },
        { LanguageCodes.ZhHans, [25.9, 25.9, 23.5, 30.5, 30.5, 25.9, 25.9, 48.4], 72.2 },
        { LanguageCodes.En, [34.9, 55.6, 36.5, 43.5, 43.5, 50.5, 14.6, 53.5], 84.7 },
        { LanguageCodes.Ja, [25.9, 25.9, 23.5, 30.5, 30.5, 25.9, 38.9, 61.3], 117.6 },
    };

    [Theory]
    [MemberData(nameof(Labels))]
    public void TheMinimumWindowNeverWraps(string language, double[] labels, double export)
    {
        var density = HistoryToolbar.Fit(MinimumToolbar, labels, HistoryToolbar.PickerMinWidth, ExportIcon, export);
        var width = HistoryToolbar.Width(density, labels, HistoryToolbar.PickerMinWidth, ExportIcon, export);
        Assert.True(width <= MinimumToolbar, $"{language}: {density} needs {width:0.#} of {MinimumToolbar}");
        Assert.False(HistoryToolbar.ShowsExportLabel(density), language);
        Assert.True(HistoryToolbar.Width(ToolbarDensity.Tight, labels, HistoryToolbar.PickerMinWidth, ExportIcon, export) <= MinimumToolbar - 24,
            $"{language} keeps room to spare at the tightest density");
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public void TheDefaultWindowShowsEverything(string language, double[] labels, double export)
    {
        var density = HistoryToolbar.Fit(DefaultToolbar, labels, HistoryToolbar.PickerMinWidth, ExportIcon, export);
        Assert.True(density == ToolbarDensity.Full, $"{language}: {density}");
    }

    [Fact]
    public void ChineseNeedsOnlyTheIconExportAtTheMinimumWidth()
    {
        var (_, labels, export) = Row(LanguageCodes.ZhHant);
        Assert.Equal(ToolbarDensity.IconExport, HistoryToolbar.Fit(MinimumToolbar, labels, HistoryToolbar.PickerMinWidth, ExportIcon, export));
    }

    [Fact]
    public void DensitiesNarrowStepByStep()
    {
        var (_, labels, export) = Row(LanguageCodes.En);
        var widths = Enum.GetValues<ToolbarDensity>()
            .Select(d => HistoryToolbar.Width(d, labels, HistoryToolbar.PickerMinWidth, ExportIcon, export))
            .ToArray();
        for (var i = 1; i < widths.Length; i++)
        {
            Assert.True(widths[i] < widths[i - 1], $"{(ToolbarDensity)i} is narrower than {(ToolbarDensity)(i - 1)}");
        }

        foreach (var density in Enum.GetValues<ToolbarDensity>())
        {
            Assert.Equal(density, HistoryToolbar.Fit(widths[(int)density], labels, HistoryToolbar.PickerMinWidth, ExportIcon, export));
        }
    }

    [Fact]
    public void ItemsKeepTheirMinimumWidthAndPadding()
    {
        Assert.Equal(HistoryToolbar.RangeChrome + 40 + 41 + 52, HistoryToolbar.RangeWidth(ToolbarDensity.Full, [5, 20.2, 32]));
        Assert.Equal(HistoryToolbar.RangeChrome + 32 + 33 + 44, HistoryToolbar.RangeWidth(ToolbarDensity.CompactRange, [5, 20.2, 32]));
        Assert.Equal(HistoryToolbar.RangeChrome + 28 + 29 + 40, HistoryToolbar.RangeWidth(ToolbarDensity.Tight, [5, 20.2, 32]));
        Assert.Equal(HistoryToolbar.RangeChrome, HistoryToolbar.RangeWidth(ToolbarDensity.Full, []));
    }

    [Fact]
    public void AWidePickerIsTakenIntoAccountUntilItNarrows()
    {
        Assert.Equal(HistoryToolbar.PickerMinWidth, HistoryToolbar.PickerWidth(ToolbarDensity.Full, 120));
        Assert.Equal(220, HistoryToolbar.PickerWidth(ToolbarDensity.CompactRange, 220));
        Assert.Equal(HistoryToolbar.CompactPickerWidth, HistoryToolbar.PickerWidth(ToolbarDensity.CompactPicker, 220));
        Assert.Equal(HistoryToolbar.CompactPickerWidth, HistoryToolbar.PickerWidth(ToolbarDensity.Tight, double.NaN));
        Assert.Equal(HistoryToolbar.PickerMinWidth, HistoryToolbar.PickerWidth(ToolbarDensity.Full, double.NaN));
    }

    [Fact]
    public void NothingFitsGivesTheTightestDensity()
    {
        Assert.Equal(ToolbarDensity.Tight, HistoryToolbar.Fit(100, [60, 60, 60], HistoryToolbar.PickerMinWidth, ExportIcon, 80));
        Assert.Equal(ToolbarDensity.Tight, HistoryToolbar.Fit(double.NaN, [60], HistoryToolbar.PickerMinWidth, ExportIcon, 80));
    }

    /// <summary>The toolbar of the minimum window that must hold the subscriptions toggle too.</summary>
    private const double MinimumWithSubscriptions = MinimumToolbar;

    /// <summary>The subscriptions toggle as an icon only.</summary>
    private const double SubscriptionsIcon = 32;

    /// <summary>The <c>history.includeSubscriptions</c> label of each language at the toolbar's font size.</summary>
    public static TheoryData<string, double> SubscriptionLabels => new()
    {
        { LanguageCodes.ZhHant, 150 },
        { LanguageCodes.ZhHans, 150 },
        { LanguageCodes.En, 230 },
        { LanguageCodes.Ja, 200 },
    };

    /// <summary>The checkbox: a 20 DIP box, 8 DIP spacing and its label.</summary>
    private static double Checkbox(double label) => 20 + 8 + label;

    [Theory]
    [MemberData(nameof(SubscriptionLabels))]
    public void TheMinimumWindowNeverWrapsWithTheSubscriptionsToggle(string language, double label)
    {
        var (_, labels, export) = Row(language);
        var density = HistoryToolbar.Fit(MinimumWithSubscriptions, labels, HistoryToolbar.PickerMinWidth, ExportIcon, export,
            Checkbox(label), SubscriptionsIcon);
        var width = HistoryToolbar.Width(density, labels, HistoryToolbar.PickerMinWidth, ExportIcon, export, Checkbox(label), SubscriptionsIcon);
        Assert.True(width <= MinimumWithSubscriptions, $"{language}: {density} needs {width:0.#} of {MinimumWithSubscriptions}");
    }

    [Fact]
    public void TheDefaultWindowShowsTheSubscriptionsLabelInChinese()
    {
        var (_, labels, export) = Row(LanguageCodes.ZhHant);
        var density = HistoryToolbar.Fit(DefaultToolbar, labels, HistoryToolbar.PickerMinWidth, ExportIcon, export, Checkbox(150), SubscriptionsIcon);
        Assert.True(HistoryToolbar.ShowsSubscriptionsLabel(density), density.ToString());
        Assert.True(HistoryToolbar.Width(density, labels, HistoryToolbar.PickerMinWidth, ExportIcon, export, Checkbox(150), SubscriptionsIcon)
            <= DefaultToolbar);
    }

    [Theory]
    [InlineData(ToolbarDensity.Full, true)]
    [InlineData(ToolbarDensity.IconExport, true)]
    [InlineData(ToolbarDensity.CompactRange, false)]
    [InlineData(ToolbarDensity.CompactPicker, false)]
    [InlineData(ToolbarDensity.Tight, false)]
    public void TheSubscriptionsLabelGivesWayToAnIconOnceTheRangeNarrows(ToolbarDensity density, bool label)
    {
        Assert.Equal(label, HistoryToolbar.ShowsSubscriptionsLabel(density));
        var icon = density == ToolbarDensity.Tight ? HistoryToolbar.TightSubscriptionsIconWidth : SubscriptionsIcon;
        Assert.Equal(HistoryToolbar.SubscriptionsGap(density) + (label ? 178 : icon), HistoryToolbar.SubscriptionsWidth(density, 178, SubscriptionsIcon));
    }

    [Fact]
    public void TheSubscriptionsToggleTakesNoRoomWhenItIsNotOffered()
    {
        Assert.Equal(0, HistoryToolbar.SubscriptionsWidth(ToolbarDensity.Full, 0, 0));
        Assert.Equal(0, HistoryToolbar.SubscriptionsWidth(ToolbarDensity.Full, double.NaN, SubscriptionsIcon));
        Assert.Equal(0, HistoryToolbar.SubscriptionsWidth(ToolbarDensity.Tight, 178, 0));
        var (_, labels, export) = Row(LanguageCodes.En);
        foreach (var density in Enum.GetValues<ToolbarDensity>())
        {
            Assert.Equal(HistoryToolbar.Width(density, labels, HistoryToolbar.PickerMinWidth, ExportIcon, export),
                HistoryToolbar.Width(density, labels, HistoryToolbar.PickerMinWidth, ExportIcon, export, 0, 0));
        }
    }

    [Fact]
    public void DensitiesStillNarrowStepByStepWithTheSubscriptionsToggle()
    {
        var (_, labels, export) = Row(LanguageCodes.En);
        var widths = Enum.GetValues<ToolbarDensity>()
            .Select(d => HistoryToolbar.Width(d, labels, HistoryToolbar.PickerMinWidth, ExportIcon, export, Checkbox(230), SubscriptionsIcon))
            .ToArray();
        for (var i = 1; i < widths.Length; i++)
        {
            Assert.True(widths[i] < widths[i - 1], $"{(ToolbarDensity)i} is narrower than {(ToolbarDensity)(i - 1)}");
        }

        foreach (var density in Enum.GetValues<ToolbarDensity>())
        {
            Assert.Equal(density, HistoryToolbar.Fit(widths[(int)density], labels, HistoryToolbar.PickerMinWidth, ExportIcon, export,
                Checkbox(230), SubscriptionsIcon));
        }
    }

    private static (string Language, double[] Labels, double Export) Row(string language)
    {
        foreach (var row in Labels)
        {
            if ((string)row[0] == language)
            {
                return (language, (double[])row[1], (double)row[2]);
            }
        }

        throw new ArgumentOutOfRangeException(nameof(language));
    }
}
