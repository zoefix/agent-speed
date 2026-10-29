using AgentSpeed.Core;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Menus;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Presentation.Tests.TestSupport;
using AgentSpeed.Presentation.Updates;

namespace AgentSpeed.Presentation.Tests;

public class OrbMenuModelTests
{
    private const string Separator = "—";

    private static readonly Localizer En = new(Localizer.LoadEmbeddedTable(), LanguageCodes.En);

    private static OrbDefinition Orb(string id, params SourceKind[] sources) =>
        new(id) { Sources = sources.Length == 0 ? OrbSources.All : OrbSources.Of(SourceSet.Create(sources)) };

    private static AppSettings With(params OrbDefinition[] orbs) => AppSettings.Default with { Orbs = new OrbsDocument(orbs) };

    /// <summary>An all-sources orb <c>o0</c> followed by single-source orbs.</summary>
    private static AppSettings Many(int count) =>
        With([Orb("o0"), .. Enumerable.Range(1, count - 1).Select(i => Orb($"o{i}", (SourceKind)(i % 8)))]);

    private static SourceAvailability Only(params SourceKind[] available) => new(SourceSet.Create(available), SourceSet.Empty);

    private static MenuLevel OrbMenu(AppSettings settings, string id = "o0", SourceAvailability? sources = null) =>
        OrbMenuModel.Build(id, settings, sources ?? SourceAvailability.All, En)!;

    private static MenuLevel QuickMenu(AppSettings settings, ReleaseInfo? update = null) =>
        QuickMenuModel.Build(settings, SourceAvailability.All, En, update);

    private static MenuRow Row(MenuLevel level, string key) => level.Rows.Single(r => r.Label == En[key]);

    private static MenuLevel Sub(MenuLevel level, string key) => Row(level, key).Submenu!;

    private static string[] Outline(MenuLevel level) =>
        [.. level.Entries.Select(e => e is MenuRow row ? row.Label : Separator)];

    private static string[] Labels(params string[] keys) => [.. keys.Select(k => k == Separator ? Separator : En[k])];

    // ---- Orb menu ----

    [Fact]
    public void TheOrbMenuFollowsTheDesignOrder()
    {
        var menu = OrbMenu(With(Orb("o0"), Orb("o1", SourceKind.Codex)));
        Assert.Equal(
            [
                En["orbs.sources.all"], Separator,
                En["orbs.style"], En["orbs.sources"], En["orbs.unit"], En["orbs.traffic"], En["orbs.showName"], En["menu.size"], En["menu.effects"], Separator,
                En["menu.alwaysOnTop"], En["menu.clickThrough"], Separator,
                En["orbs.add"], En["menu.orb.duplicate"], En["menu.orb.split"], En["menu.orb.mergeAll"], En["boards.add"], Separator,
                En["menu.orb.hide"], En["menu.orb.remove"], Separator,
                En["menu.orb.settings"], En["menu.dashboard"], En["menu.quit"],
            ],
            Outline(menu));
    }

    [Fact]
    public void AnUnknownOrbHasNoMenu() => Assert.Null(OrbMenuModel.Build("missing", With(Orb("o0")), SourceAvailability.All, En));

    [Fact]
    public void TheHeaderShowsTheOrbsDotsAndNameAndCannotBeChosen()
    {
        var settings = With(Orb("a", SourceKind.ClaudeCode, SourceKind.Codex));
        var header = OrbMenu(settings, "a", Only(SourceKind.ClaudeCode)).Rows.First();

        Assert.Equal(En.Format("orbs.name.pair", En["source.codex"], En["source.claudeCode"]), header.Label);
        Assert.Equal(new MenuIcon.Dots([new SourceDot(SourceKind.Codex, false), new SourceDot(SourceKind.ClaudeCode, true)]), header.Icon);
        Assert.True(header.IsHeader);
        Assert.False(header.IsEnabled);
        Assert.Null(header.Action);

        var named = OrbMenu(With(Orb("a") with { Name = "Main" }), "a").Rows.First();
        Assert.Equal("Main", named.Label);
        Assert.Null(named.Icon);
    }

    [Fact]
    public void WithOneOrbRemoveAndMergeAreDisabledAndHideIsTheMasterSwitch()
    {
        var menu = OrbMenu(With(Orb("o0")));

        Assert.False(Row(menu, "menu.orb.remove").IsEnabled);
        Assert.False(Row(menu, "menu.orb.mergeAll").IsEnabled);
        Assert.True(Row(menu, "orbs.add").IsEnabled);
        Assert.Null(Row(menu, "orbs.add").ToolTip);
        Assert.True(Row(menu, "menu.orb.duplicate").IsEnabled);
        Assert.Equal(new MenuAction.SetShowOrbs(false), Row(menu, "menu.hideOrb").Action);
        Assert.DoesNotContain(menu.Rows, r => r.Label == En["menu.orb.hide"]);
    }

    [Fact]
    public void WithTwoOrbsEveryOrbCanBeHiddenRemovedAndMerged()
    {
        var menu = OrbMenu(With(Orb("a"), Orb("b", SourceKind.Codex)), "a");

        Assert.Equal(new MenuAction.SetOrbVisible("a", false), Row(menu, "menu.orb.hide").Action);
        Assert.True(Row(menu, "menu.orb.remove").IsEnabled);
        Assert.Equal(new MenuAction.RemoveOrb("a"), Row(menu, "menu.orb.remove").Action);
        Assert.True(Row(menu, "menu.orb.mergeAll").IsEnabled);
        Assert.Equal(new MenuAction.MergeOrbs("a"), Row(menu, "menu.orb.mergeAll").Action);
        Assert.Equal(new MenuAction.AddOrb("a"), Row(menu, "orbs.add").Action);
        Assert.Equal(new MenuAction.DuplicateOrb("a"), Row(menu, "menu.orb.duplicate").Action);
    }

    [Fact]
    public void TheLastVisibleOrbHidesWithTheMasterSwitch()
    {
        var menu = OrbMenu(With(Orb("a"), Orb("b", SourceKind.Codex) with { Visible = false }), "a");

        Assert.Equal(new MenuAction.SetShowOrbs(false), Row(menu, "menu.hideOrb").Action);
        Assert.True(Row(menu, "menu.orb.remove").IsEnabled);
    }

    [Fact]
    public void AtEightOrbsAddDuplicateAndSplitAreDisabledWithTheLimit()
    {
        var menu = OrbMenu(Many(8));
        var limit = En.Format("orbs.limit", 8);

        foreach (var key in new[] { "orbs.add", "menu.orb.duplicate", "menu.orb.split" })
        {
            Assert.False(Row(menu, key).IsEnabled);
            Assert.Equal(limit, Row(menu, key).ToolTip);
        }

        Assert.True(Row(menu, "menu.orb.mergeAll").IsEnabled);
        Assert.True(Row(menu, "menu.orb.remove").IsEnabled);

        var seven = OrbMenu(Many(7));
        Assert.True(Row(seven, "orbs.add").IsEnabled);
        Assert.True(Row(seven, "menu.orb.split").IsEnabled);
    }

    [Fact]
    public void SplitNeedsTwoAvailableSourcesOfTheOrb()
    {
        var pair = With(Orb("a", SourceKind.Codex, SourceKind.Gemini));
        var split = Row(OrbMenu(pair, "a"), "menu.orb.split");
        Assert.True(split.IsEnabled);
        Assert.Null(split.ToolTip);
        Assert.Equal(new MenuAction.SplitOrb("a"), split.Action);

        var halfAvailable = Row(OrbMenu(pair, "a", Only(SourceKind.Codex, SourceKind.ClaudeCode)), "menu.orb.split");
        Assert.False(halfAvailable.IsEnabled);
        Assert.Null(halfAvailable.ToolTip);

        Assert.False(Row(OrbMenu(With(Orb("a", SourceKind.Codex)), "a"), "menu.orb.split").IsEnabled);
    }

    [Fact]
    public void SplittingAnAllSourcesOrbNeedsTwoDetectedSources()
    {
        var all = With(Orb("a"));
        Assert.True(Row(OrbMenu(all, "a", Only(SourceKind.Codex, SourceKind.Gemini)), "menu.orb.split").IsEnabled);

        var one = Row(OrbMenu(all, "a", Only(SourceKind.Codex)), "menu.orb.split");
        Assert.False(one.IsEnabled);
        Assert.Equal(En["orbs.split.unavailable"], one.ToolTip);

        var disabled = new SourceAvailability(SourceSet.Of(SourceKind.Codex), SourceSet.Everything.Without(SourceKind.Codex));
        Assert.False(Row(OrbMenu(all, "a", disabled), "menu.orb.split").IsEnabled);
    }

    [Fact]
    public void TheSourcesSubmenuListsAllSourcesThenEverySourceInOrder()
    {
        var sources = Sub(OrbMenu(With(Orb("a", SourceKind.Gemini, SourceKind.Codex)), "a"), "orbs.sources");

        Assert.Equal(En["orbs.sources.all"], sources.Rows.First().Label);
        Assert.False(sources.Rows.First().IsChecked);
        Assert.Equal(new MenuAction.SetOrbAllSources("a"), sources.Rows.First().Action);
        Assert.IsType<MenuSeparator>(sources.Entries[1]);
        Assert.Equal(Enum.GetValues<SourceKind>().Select(k => En[DisplayText.SourceKey(k)]), sources.Rows.Skip(1).Select(r => r.Label));
        Assert.Equal(["OpenClaw", "Hermes Agent", "Factory Droid", "Trae"], sources.Rows.TakeLast(4).Select(r => r.Label));

        foreach (var kind in Enum.GetValues<SourceKind>())
        {
            var row = sources.Rows.Single(r => r.Label == En[DisplayText.SourceKey(kind)]);
            var tracked = kind is SourceKind.Gemini or SourceKind.Codex;
            Assert.True(row.IsCheckable);
            Assert.Equal(tracked, row.IsChecked);
            Assert.True(row.IsEnabled);
            Assert.Equal(new MenuAction.ToggleOrbSource("a", kind), row.Action);
            Assert.Equal(new MenuIcon.Dots([new SourceDot(kind, true)]), row.Icon);
        }
    }

    [Fact]
    public void TheLastTrackedSourceCannotBeUnchecked()
    {
        var sources = Sub(OrbMenu(With(Orb("a", SourceKind.Gemini)), "a"), "orbs.sources");
        var gemini = sources.Rows.Single(r => r.Label == En["source.gemini"]);
        var codex = sources.Rows.Single(r => r.Label == En["source.codex"]);

        Assert.True(gemini.IsChecked);
        Assert.False(gemini.IsEnabled);
        Assert.Equal(En["orbs.sources.lastOne"], gemini.ToolTip);
        Assert.False(codex.IsChecked);
        Assert.True(codex.IsEnabled);
        Assert.Null(codex.ToolTip);
    }

    [Fact]
    public void AnAllSourcesOrbChecksAllSourcesAndNoSingleSource()
    {
        var sources = Sub(OrbMenu(With(Orb("a")), "a"), "orbs.sources");

        Assert.True(sources.Rows.First().IsChecked);
        Assert.All(sources.Rows.Skip(1), row =>
        {
            Assert.False(row.IsChecked);
            Assert.True(row.IsEnabled);
        });
    }

    [Fact]
    public void UnavailableSourcesSayWhyAndStaySelectable()
    {
        var availability = new SourceAvailability(
            SourceSet.Everything.Without(SourceKind.Omp).Without(SourceKind.Gemini), SourceSet.Of(SourceKind.Omp));
        var sources = Sub(OrbMenu(With(Orb("a", SourceKind.Omp, SourceKind.Gemini)), "a", availability), "orbs.sources");
        var omp = sources.Rows.Single(r => r.Label == En["source.omp"]);
        var gemini = sources.Rows.Single(r => r.Label == En["source.gemini"]);
        var codex = sources.Rows.Single(r => r.Label == En["source.codex"]);

        Assert.Equal(En["source.disabled"], omp.Detail);
        Assert.Equal(En["source.notFound"], gemini.Detail);
        Assert.Null(codex.Detail);
        Assert.True(omp.IsEnabled);
        Assert.True(gemini.IsEnabled);
        Assert.Equal(new MenuIcon.Dots([new SourceDot(SourceKind.Omp, false)]), omp.Icon);
    }

    [Fact]
    public void StyleSizeAndEffectsCheckTheCurrentChoice()
    {
        var orb = Orb("a") with { Style = OrbStyle.Needle, Size = 150 };
        var menu = OrbMenu(With(orb) with { Effects = EffectsIntensity.Max }, "a");

        var styles = Sub(menu, "orbs.style");
        Assert.Equal(OrbStyles.All.Select(s => En["style." + OrbStyles.Key(s)]), styles.Rows.Select(r => r.Label));
        Assert.Equal([En["style.needle"]], styles.Rows.Where(r => r.IsChecked).Select(r => r.Label));
        Assert.Equal(new MenuAction.SetOrbStyle("a", OrbStyle.Liquid), styles.Rows.Single(r => r.Label == En["style.liquid"]).Action);

        var sizes = Sub(menu, "menu.size");
        Assert.Equal(Labels("menu.size.small", "menu.size.medium", "menu.size.large"), Outline(sizes));
        Assert.Equal([En["menu.size.medium"]], sizes.Rows.Where(r => r.IsChecked).Select(r => r.Label));
        Assert.Equal(
            [new MenuAction.SetOrbSize("a", 112), new MenuAction.SetOrbSize("a", 148), new MenuAction.SetOrbSize("a", 196)],
            sizes.Rows.Select(r => r.Action));

        var effects = Sub(menu, "menu.effects");
        Assert.Equal(Labels("effects.off", "effects.soft", "effects.standard", "effects.max"), Outline(effects));
        Assert.Equal([En["effects.max"]], effects.Rows.Where(r => r.IsChecked).Select(r => r.Label));
        Assert.Equal(new MenuAction.SetEffects(EffectsIntensity.Soft), effects.Rows.Single(r => r.Label == En["effects.soft"]).Action);
    }

    [Theory]
    [InlineData(112, 0)]
    [InlineData(148, 1)]
    [InlineData(141, 1)]
    [InlineData(155, 1)]
    [InlineData(196, 2)]
    [InlineData(240, -1)]
    [InlineData(170, -1)]
    [InlineData(72, -1)]
    public void ASizeChecksItsNearestPresetWithinEight(double size, int expected) =>
        Assert.Equal(expected, OrbMenuModel.PresetIndex(size));

    [Fact]
    public void WindowTogglesActOnThisOrb()
    {
        var menu = OrbMenu(With(Orb("a") with { AlwaysOnTop = false, ClickThrough = true }), "a");

        Assert.False(Row(menu, "menu.alwaysOnTop").IsChecked);
        Assert.Equal(new MenuAction.SetOrbAlwaysOnTop("a", true), Row(menu, "menu.alwaysOnTop").Action);
        Assert.True(Row(menu, "menu.clickThrough").IsChecked);
        Assert.Equal(new MenuAction.SetOrbClickThrough("a", false), Row(menu, "menu.clickThrough").Action);
    }

    [Fact]
    public void TheLastGroupOpensSettingsTheDashboardAndQuits()
    {
        var menu = OrbMenu(With(Orb("a")), "a");

        Assert.Equal(new MenuAction.OpenOrbSettings("a"), Row(menu, "menu.orb.settings").Action);
        Assert.Equal(new MenuIcon.Glyph(MenuGlyph.Settings), Row(menu, "menu.orb.settings").Icon);
        Assert.Equal(new MenuAction.OpenDashboard(), Row(menu, "menu.dashboard").Action);
        Assert.Equal(new MenuIcon.Glyph(MenuGlyph.Dashboard), Row(menu, "menu.dashboard").Icon);
        Assert.Equal(new MenuAction.Quit(), Row(menu, "menu.quit").Action);
        Assert.Equal(new MenuIcon.Glyph(MenuGlyph.Quit), Row(menu, "menu.quit").Icon);
    }

    [Fact]
    public void EachLevelReservesItsOwnColumns()
    {
        var menu = OrbMenu(With(Orb("a", SourceKind.Codex)), "a");

        Assert.True(menu.ReservesIconColumn);
        Assert.True(menu.ReservesTrailingColumn);
        Assert.False(Sub(menu, "orbs.style").ReservesIconColumn);
        Assert.True(Sub(menu, "orbs.style").ReservesTrailingColumn);
        Assert.True(Sub(menu, "orbs.sources").ReservesIconColumn);
        Assert.False(Sub(menu, "menu.size").ReservesIconColumn);
        Assert.False(Sub(menu, "menu.effects").ReservesIconColumn);

        var plain = new MenuLevel([new MenuRow("A") { Action = new MenuAction.Quit() }, MenuSeparator.Instance, new MenuRow("B")]);
        Assert.False(plain.ReservesIconColumn);
        Assert.False(plain.ReservesTrailingColumn);
    }

    // ---- Quick menu ----

    [Fact]
    public void WithOneOrbTheQuickMenuKeepsTodaysItemsForThatOrb()
    {
        var settings = With(Orb("solo") with { Size = 196, ClickThrough = true });
        var menu = QuickMenu(settings);

        Assert.Equal(
            [
                En["menu.dashboard"], En["history.open"], Separator,
                En["menu.size"], En["menu.effects"], En["menu.alwaysOnTop"], En["menu.clickThrough"], En["menu.hideOrb"],
                En["boards.add"], En["settings.bar.show"], Separator,
                En["menu.settings"], En["menu.about"], En["update.check"] + "…", Separator, En["menu.quit"],
            ],
            Outline(menu));
        Assert.True(Row(menu, "menu.dashboard").IsDefault);
        Assert.Equal(new MenuAction.OpenHistory(), Row(menu, "history.open").Action);
        Assert.Equal([En["menu.size.large"]], Sub(menu, "menu.size").Rows.Where(r => r.IsChecked).Select(r => r.Label));
        Assert.Equal(new MenuAction.SetOrbSize("solo", 112), Sub(menu, "menu.size").Rows.First().Action);
        Assert.Equal(new MenuAction.SetOrbAlwaysOnTop("solo", false), Row(menu, "menu.alwaysOnTop").Action);
        Assert.Equal(new MenuAction.SetOrbClickThrough("solo", false), Row(menu, "menu.clickThrough").Action);
        Assert.Equal(new MenuAction.SetShowOrbs(false), Row(menu, "menu.hideOrb").Action);
        Assert.Equal(new MenuAction.SetShowOnTaskbar(false), Row(menu, "settings.bar.show").Action);
        Assert.Equal(new MenuAction.OpenSettings("about"), Row(menu, "menu.about").Action);
        Assert.All(new[] { "menu.size", "menu.alwaysOnTop", "menu.clickThrough" }, key => Assert.True(Row(menu, key).IsEnabled));
        Assert.DoesNotContain(menu.Rows, r => r.Label == En["settings.orb"]);
    }

    [Fact]
    public void AHiddenSingleOrbOffersShowAndDisablesItsItems()
    {
        var menu = QuickMenu(With(Orb("solo")) with { ShowOrbs = false });

        Assert.Equal(new MenuAction.SetShowOrbs(true), Row(menu, "menu.showOrb").Action);
        Assert.All(new[] { "menu.size", "menu.alwaysOnTop", "menu.clickThrough" }, key => Assert.False(Row(menu, key).IsEnabled));
        Assert.True(Row(menu, "menu.effects").IsEnabled);
    }

    [Fact]
    public void WithSeveralOrbsTheQuickMenuListsEachOrb()
    {
        var settings = With(Orb("a"), Orb("b", SourceKind.Codex, SourceKind.Gemini) with { Visible = false, ClickThrough = true });
        var menu = QuickMenu(settings);

        Assert.Equal(
            [
                En["menu.dashboard"], En["history.open"], Separator,
                En["settings.orb"], En["menu.effects"], En["menu.hideOrbs"], En["boards.add"], En["settings.bar.show"], Separator,
                En["menu.settings"], En["menu.about"], En["update.check"] + "…", Separator, En["menu.quit"],
            ],
            Outline(menu));
        Assert.Equal(new MenuAction.SetShowOrbs(false), Row(menu, "menu.hideOrbs").Action);

        var orbs = Sub(menu, "settings.orb");
        Assert.True(orbs.ReservesIconColumn);
        Assert.Equal([En["orbs.sources.all"], En.Format("orbs.name.pair", En["source.codex"], En["source.gemini"])], Outline(orbs));

        var first = orbs.Rows.First();
        Assert.Null(first.Icon);
        Assert.Null(first.Detail);
        Assert.Equal(Labels("orbs.visible", "menu.clickThrough", "orbs.showName", "orbs.unit", "orbs.traffic", Separator, "menu.orb.settings"), Outline(first.Submenu!));
        Assert.True(Row(first.Submenu!, "orbs.visible").IsChecked);
        Assert.Equal(new MenuAction.SetOrbVisible("a", false), Row(first.Submenu!, "orbs.visible").Action);

        var second = orbs.Rows.Last();
        Assert.Equal(new MenuIcon.Dots([new SourceDot(SourceKind.Codex, true), new SourceDot(SourceKind.Gemini, true)]), second.Icon);
        Assert.Equal(En["orbs.hidden"], second.Detail);
        Assert.False(Row(second.Submenu!, "orbs.visible").IsChecked);
        Assert.Equal(new MenuAction.SetOrbVisible("b", true), Row(second.Submenu!, "orbs.visible").Action);
        Assert.True(Row(second.Submenu!, "menu.clickThrough").IsChecked);
        Assert.Equal(new MenuAction.SetOrbClickThrough("b", false), Row(second.Submenu!, "menu.clickThrough").Action);
        Assert.Equal(new MenuAction.OpenOrbSettings("b"), Row(second.Submenu!, "menu.orb.settings").Action);
    }

    [Fact]
    public void TheMasterSwitchShowsOrbsThatAreAllHiddenOrSwitchedOff()
    {
        var allHidden = QuickMenu(With(Orb("a") with { Visible = false }, Orb("b", SourceKind.Codex) with { Visible = false }));
        Assert.Equal(new MenuAction.SetShowOrbs(true), Row(allHidden, "menu.showOrbs").Action);

        var off = QuickMenu(With(Orb("a"), Orb("b", SourceKind.Codex)) with { ShowOrbs = false });
        Assert.Equal(new MenuAction.SetShowOrbs(true), Row(off, "menu.showOrbs").Action);
        var orbs = Sub(off, "settings.orb");
        Assert.All(orbs.Rows, row => Assert.False(row.Submenu!.Rows.First().IsChecked));
        Assert.Equal(new MenuAction.SetOrbVisible("a", true), orbs.Rows.First().Submenu!.Rows.First().Action);

        Assert.Equal("menu.hideOrb", QuickMenuModel.MasterSwitchKey(1, true));
        Assert.Equal("menu.showOrb", QuickMenuModel.MasterSwitchKey(1, false));
        Assert.Equal("menu.hideOrbs", QuickMenuModel.MasterSwitchKey(3, true));
        Assert.Equal("menu.showOrbs", QuickMenuModel.MasterSwitchKey(3, false));
    }

    [Fact]
    public void AnAvailableUpdateComesFirst()
    {
        var release = TestReleases.Release("v9.1.0");
        var menu = QuickMenu(With(Orb("a")), release);

        Assert.Equal(UpdateText.MenuItem(release, En), menu.Rows.First().Label);
        Assert.Equal(new MenuIcon.AccentDot(), menu.Rows.First().Icon);
        Assert.Equal(new MenuAction.OpenUpdate(), menu.Rows.First().Action);
        Assert.IsType<MenuSeparator>(menu.Entries[1]);
        Assert.Equal(En["menu.dashboard"], Assert.IsType<MenuRow>(menu.Entries[2]).Label);
    }

    [Fact]
    public void EveryQuickMenuLevelReservesTheIconColumnOnlyWhenItHasIcons()
    {
        var menu = QuickMenu(With(Orb("a", SourceKind.Codex), Orb("b", SourceKind.Gemini)));

        Assert.True(menu.ReservesIconColumn);
        Assert.False(Sub(menu, "menu.effects").ReservesIconColumn);
        Assert.True(Sub(menu, "settings.orb").ReservesIconColumn);
        Assert.True(Sub(menu, "settings.orb").Rows.First().Submenu!.ReservesIconColumn);

        var allSources = QuickMenu(With(Orb("a"), Orb("b")));
        Assert.False(Sub(allSources, "settings.orb").ReservesIconColumn);
    }

    // ---- Availability and dots ----

    [Fact]
    public void AvailabilityFollowsTheOrbRule()
    {
        static SourceStatus Status(SourceKind kind, bool enabled = true, bool available = true, long requests = 0) =>
            new(kind, enabled, string.Empty, available, 0, Rate.Zero, new TokenTotals(Requests: requests), 0);

        var availability = SourceAvailability.From(
        [
            Status(SourceKind.Codex),
            Status(SourceKind.Gemini, available: false),
            Status(SourceKind.Omp, enabled: false),
            Status(SourceKind.DeepSeek, available: false, requests: 3),
        ]);

        Assert.Equal(SourceState.Available, availability.StateOf(SourceKind.Codex));
        Assert.Equal(SourceState.NotFound, availability.StateOf(SourceKind.Gemini));
        Assert.Equal(SourceState.Disabled, availability.StateOf(SourceKind.Omp));
        Assert.Equal(SourceState.Available, availability.StateOf(SourceKind.DeepSeek));
        Assert.Equal(SourceState.Available, availability.StateOf(SourceKind.Antigravity));
        Assert.Equal(SourceSet.Everything, SourceAvailability.All.Available);
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(2, 6)]
    [InlineData(3, 6)]
    [InlineData(4, 6)]
    [InlineData(5, 4)]
    [InlineData(8, 4)]
    [InlineData(9, 4)]
    public void DotsFitTheIconBoxCenteredWithoutOverlapping(int count, double diameter)
    {
        var dots = MenuDotLayout.Layout(count);

        Assert.Equal(count, dots.Count);
        Assert.All(dots, d => Assert.Equal(diameter, d.Diameter));
        var left = dots.Min(d => d.X - d.Diameter / 2);
        var right = dots.Max(d => d.X + d.Diameter / 2);
        var top = dots.Min(d => d.Y - d.Diameter / 2);
        var bottom = dots.Max(d => d.Y + d.Diameter / 2);
        Assert.True(left >= 0 && top >= 0 && right <= MenuDotLayout.Box && bottom <= MenuDotLayout.Box);
        Assert.Equal(MenuDotLayout.Box / 2, (left + right) / 2, 6);
        Assert.Equal(MenuDotLayout.Box / 2, (top + bottom) / 2, 6);
        Assert.All(dots, d => Assert.Equal(Math.Round(d.X - d.Diameter / 2), d.X - d.Diameter / 2));
        for (var i = 0; i < dots.Count; i++)
        {
            for (var j = i + 1; j < dots.Count; j++)
            {
                var distance = Math.Sqrt(Math.Pow(dots[i].X - dots[j].X, 2) + Math.Pow(dots[i].Y - dots[j].Y, 2));
                Assert.True(distance >= diameter + 2 - 1e-9, $"dots {i} and {j} of {count} touch");
            }
        }
    }

    [Fact]
    public void DotsBeyondNineAreDropped()
    {
        Assert.Empty(MenuDotLayout.Layout(0));
        Assert.Equal(MenuDotLayout.MaxDots, MenuDotLayout.Layout(12).Count);
    }

    // ---- Name plate ----

    [Fact]
    public void ShowNameIsCheckedWhenThePlateShowsAndStoresTheOpposite()
    {
        var two = With(Orb("a"), Orb("b", SourceKind.Codex));
        var row = Row(OrbMenu(two, "a"), "orbs.showName");
        Assert.True(row.IsCheckable);
        Assert.True(row.IsChecked);
        Assert.True(row.IsEnabled);
        Assert.Equal(new MenuAction.SetOrbShowName("a", false), row.Action);

        var single = Row(OrbMenu(With(Orb("o0"))), "orbs.showName");
        Assert.False(single.IsChecked);
        Assert.Equal(new MenuAction.SetOrbShowName("o0", true), single.Action);
        Assert.True(Row(OrbMenu(With(Orb("s", SourceKind.Codex)), "s"), "orbs.showName").IsChecked);

        var forcedOff = With(Orb("a"), Orb("b", SourceKind.Codex) with { ShowName = false });
        Assert.False(Row(OrbMenu(forcedOff, "b"), "orbs.showName").IsChecked);
        Assert.Equal(new MenuAction.SetOrbShowName("b", true), Row(OrbMenu(forcedOff, "b"), "orbs.showName").Action);

        var never = With(Orb("a"), Orb("b")) with { OrbNames = OrbNamesMode.Never };
        Assert.False(Row(OrbMenu(never, "a"), "orbs.showName").IsChecked);
        var always = With(Orb("a")) with { OrbNames = OrbNamesMode.Always };
        Assert.True(Row(OrbMenu(always, "a"), "orbs.showName").IsChecked);
    }

    [Fact]
    public void ShowNameIsDisabledBelowEightyPoints()
    {
        var small = With(Orb("a") with { Style = OrbStyle.Minimal, Size = 72, ShowName = true }, Orb("b"));
        var row = Row(OrbMenu(small, "a"), "orbs.showName");
        Assert.False(row.IsEnabled);
        Assert.False(row.IsChecked);
    }

    [Fact]
    public void TheQuickMenusOrbSubmenuHasShowName()
    {
        var settings = With(Orb("a"), Orb("b", SourceKind.Codex) with { Visible = false });
        var orbs = Sub(QuickMenu(settings), "settings.orb");
        var a = Row(orbs.Rows.First().Submenu!, "orbs.showName");
        var b = Row(orbs.Rows.Last().Submenu!, "orbs.showName");

        // "a" is the only visible orb and tracks all sources; hidden "b" counts as shown for its own check.
        Assert.False(a.IsChecked);
        Assert.Equal(new MenuAction.SetOrbShowName("a", true), a.Action);
        Assert.True(b.IsChecked);
        Assert.Equal(new MenuAction.SetOrbShowName("b", false), b.Action);
        Assert.True(orbs.Rows.First().Submenu!.ReservesTrailingColumn);
    }
}
