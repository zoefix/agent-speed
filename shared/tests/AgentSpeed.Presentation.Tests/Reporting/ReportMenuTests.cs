using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Menus;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Tests.Reporting;

public class ReportMenuTests
{
    private const string Separator = "—";

    public static TheoryData<string> Languages => [LanguageCodes.ZhHant, LanguageCodes.ZhHans, LanguageCodes.En, LanguageCodes.Ja];

    private static Localizer Loc(string language) => new(Localizer.LoadEmbeddedTable(), language);

    private static MenuLevel Quick(Localizer loc, ReportMenuState? report) =>
        QuickMenuModel.Build(AppSettings.Default, SourceAvailability.All, loc, report: report);

    private static string[] Outline(MenuLevel level) => [.. level.Entries.Select(e => e is MenuRow row ? row.Label : Separator)];

    [Theory]
    [MemberData(nameof(Languages))]
    public void WithoutReportingTheMenuHasNoReportItem(string language)
    {
        var loc = Loc(language);
        var menu = Quick(loc, null);
        Assert.DoesNotContain(menu.Rows, r => r.Icon is MenuIcon.StatusDot);
        Assert.Equal(Outline(QuickMenuModel.Build(AppSettings.Default, SourceAvailability.All, loc)), Outline(menu));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void TheReportItemFollowsHistoryBetweenSeparators(string language)
    {
        var loc = Loc(language);
        var without = Outline(Quick(loc, null));
        var with = Outline(Quick(loc, new ReportMenuState("Codex Radar", Paused: false, HasLeaderboard: true, MenuStatusTone.Green)));
        var history = Array.IndexOf(with, loc["history.open"]);
        Assert.True(history >= 0);
        Assert.Equal(Separator, with[history + 1]);
        Assert.Equal(loc.Format("report.menu.on", "Codex Radar"), with[history + 2]);
        Assert.Equal(Separator, with[history + 3]);
        Assert.Equal(without.Length + 2, with.Length);
        Assert.Equal(without, with.Where((_, i) => i != history + 1 && i != history + 2).ToArray());
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void TheReportSubmenuPausesAndOpensTheLeaderboardAndSettings(string language)
    {
        var loc = Loc(language);
        var row = QuickMenuModel.ReportMenu(new ReportMenuState("速蹬雷达", Paused: false, HasLeaderboard: true, MenuStatusTone.Amber), loc);
        Assert.Equal(loc.Format("report.menu.on", "速蹬雷达"), row.Label);
        Assert.Equal(new MenuIcon.StatusDot(MenuStatusTone.Amber), row.Icon);
        Assert.Null(row.Action);
        var sub = row.Submenu!;
        Assert.Equal(
            new[]
            {
                loc["report.pause.hour"], loc["report.pause.indefinite"], Separator, loc["report.service.leaderboard"], Separator,
                loc["report.menu.settings"],
            },
            Outline(sub));
        var rows = sub.Rows.ToArray();
        Assert.Equal(new MenuAction.PauseReporting(Indefinitely: false), rows[0].Action);
        Assert.Equal(new MenuAction.PauseReporting(Indefinitely: true), rows[1].Action);
        Assert.Equal(new MenuAction.OpenLeaderboard(), rows[2].Action);
        Assert.Equal(new MenuAction.OpenSettings("report"), rows[3].Action);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void WhilePausedTheItemSaysSoAndOffersResume(string language)
    {
        var loc = Loc(language);
        var row = QuickMenuModel.ReportMenu(new ReportMenuState("Codex Radar", Paused: true, HasLeaderboard: false, MenuStatusTone.Gray), loc);
        Assert.Equal(loc["report.menu.paused"], row.Label);
        Assert.Equal(new MenuIcon.StatusDot(MenuStatusTone.Gray), row.Icon);
        Assert.Equal(new[] { loc["report.resume"], Separator, loc["report.menu.settings"] }, Outline(row.Submenu!));
        Assert.Equal(new MenuAction.ResumeReporting(), row.Submenu!.Rows.First().Action);
        Assert.DoesNotContain(row.Submenu.Rows, r => r.Action is MenuAction.OpenLeaderboard);
    }

    [Theory]
    [InlineData(MenuStatusTone.Green)]
    [InlineData(MenuStatusTone.Gray)]
    [InlineData(MenuStatusTone.Amber)]
    [InlineData(MenuStatusTone.Red)]
    public void TheDotCarriesTheStatusTone(MenuStatusTone tone)
    {
        var menu = Quick(Loc(LanguageCodes.En), new ReportMenuState("x", false, false, tone));
        Assert.Equal(new MenuIcon.StatusDot(tone), menu.Rows.Single(r => r.Icon is MenuIcon.StatusDot).Icon);
        Assert.True(menu.ReservesIconColumn);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryReportMenuLabelIsTranslated(string language)
    {
        var loc = Loc(language);
        string[] keys = ["report.menu.on", "report.menu.paused", "report.menu.settings", "report.pause.hour", "report.pause.indefinite",
            "report.resume", "report.service.leaderboard"];
        foreach (var key in keys)
        {
            Assert.NotEqual(key, loc[key]);
            Assert.False(string.IsNullOrWhiteSpace(loc[key]));
        }

        foreach (var paused in new[] { false, true })
        {
            var row = QuickMenuModel.ReportMenu(new ReportMenuState("Svc", paused, HasLeaderboard: true, MenuStatusTone.Red), loc);
            foreach (var label in Outline(row.Submenu!).Append(row.Label))
            {
                Assert.DoesNotContain("report.", label, StringComparison.Ordinal);
                Assert.DoesNotContain("%@", label, StringComparison.Ordinal);
            }
        }
    }
}
