using AgentSpeed.Core;
using AgentSpeed.Core.Money;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Menus;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Tests;

public class OrbMoneyTests
{
    private static readonly Localizer En = new(Localizer.LoadEmbeddedTable(), LanguageCodes.En);

    private static SpeedSnapshot Snapshot(double codexCost, double claudeCost) => SpeedSnapshot.Empty with
    {
        Sources =
        [
            new SourceStatus(SourceKind.Codex, true, "", true, 1, new Rate(100, 100), new TokenTotals(Requests: 3, Output: 300), 1)
            {
                LiveCost = new LiveCost(codexCost, codexCost > 0 ? 100 : 0),
                TodayCost = new CostTally(2_000_000),
            },
            new SourceStatus(SourceKind.ClaudeCode, true, "", true, 1, new Rate(50, 50), new TokenTotals(Requests: 2, Output: 200), 1)
            {
                LiveCost = new LiveCost(claudeCost, claudeCost > 0 ? 50 : 0),
                TodayCost = new CostTally(1_000_000),
            },
        ],
    };

    [Fact]
    public void AnOrbInMoneyModeKeepsItsTokenValueForHeat()
    {
        var feed = new OrbFeed("a") { Unit = OrbUnit.Cost, Cost = new CostDisplay(En) };
        feed.Configure(SourceSet.Of(SourceKind.Codex), SpeedMetric.Output, 400, 0);
        feed.Update(Snapshot(3000, 9000), 1);
        Assert.Equal(100, feed.Value);
        Assert.Equal("0.003", feed.Money!.Number);

        feed.Unit = OrbUnit.Tokens;
        feed.Update(Snapshot(3000, 9000), 2);
        Assert.Null(feed.Money);
    }

    [Fact]
    public void SubscriptionSourcesAreLeftOutOfAnOrbsMoney()
    {
        var display = new CostDisplay(En) { Settings = CostSettings.Default with { Subscriptions = SourceSet.Of(SourceKind.ClaudeCode) } };
        var feed = new OrbFeed("a") { Unit = OrbUnit.Cost, Cost = display };
        feed.Configure(null, SpeedMetric.Output, 400, 0);
        feed.Update(Snapshot(3000, 9000), 1);
        Assert.Equal("0.003", feed.Money!.Number);

        var claude = new OrbFeed("b") { Unit = OrbUnit.Cost, Cost = display };
        claude.Configure(SourceSet.Of(SourceKind.ClaudeCode), SpeedMetric.Output, 400, 0);
        claude.Update(Snapshot(3000, 9000), 1);
        Assert.Equal("0", claude.Money!.Number);
        Assert.Equal(En["cost.subscriptionOnly"], claude.Money.HoverLine);
    }

    [Fact]
    public void TheSceneShowsMoneyTextAndSwitchesWithoutRolling()
    {
        var scene = new OrbScene(seed: 1);
        scene.Update(0);
        scene.SetTarget(120, SpeedTier.Cruise);
        scene.Update(1);
        Assert.Equal("120", scene.Readout.Current);
        Assert.Null(scene.UnitText);

        var money = new CostDisplay(En).Live(new CostReading(4200, 0.5, 120, CostTally.Zero, 0, [], [], CostState.Partial), 0);
        scene.SetMoney(money);
        Assert.Equal("0.0042", scene.Readout.Current);
        Assert.Equal("$/s", scene.UnitText);
        Assert.True(scene.ValueAtLeast);
        Assert.Equal(new UnitSwitch("120", false, null, 0), scene.UnitSwitch);
        Assert.Equal(FrameDemand.Full, scene.Demand);

        scene.SetTarget(500, SpeedTier.Boost);
        scene.Update(1.5);
        Assert.Equal("0.0042", scene.Readout.Current);
        Assert.True(scene.HeatRaw > 0);
        Assert.Null(scene.UnitSwitch);

        scene.SetMoney(null);
        Assert.Equal("500", scene.Readout.Current);
        Assert.False(scene.ValueAtLeast);
    }

    [Fact]
    public void NothingPricedIsMuted()
    {
        var scene = new OrbScene(seed: 1);
        scene.SetMoney(new CostDisplay(En).Live(new CostReading(0, 0, 10, CostTally.Zero, 0, [], [], CostState.NothingPriced), 0));
        Assert.True(scene.ValueMuted);
        Assert.Equal("–", scene.Readout.Current);
    }

    [Fact]
    public void AClickOnTheUnitTogglesAfterTheDoubleClickInterval()
    {
        var toggle = new OrbUnitToggle();
        toggle.Press(new PointD(10, 10), 1.0, onUnit: true, clickCount: 1);
        Assert.True(toggle.Release(new PointD(12, 11), 1.2, moved: false, doubleClickInterval: 0.5));
        Assert.False(toggle.IsDue(1.4));
        Assert.True(toggle.IsDue(1.5));
        Assert.False(toggle.IsDue(1.6));
    }

    [Fact]
    public void ADoubleClickADragOrALongPressNeverToggles()
    {
        var toggle = new OrbUnitToggle();
        toggle.Press(new PointD(0, 0), 0, onUnit: true, clickCount: 1);
        toggle.Release(new PointD(0, 0), 0.1, moved: false, doubleClickInterval: 0.5);
        toggle.Press(new PointD(0, 0), 0.2, onUnit: true, clickCount: 2);
        Assert.False(toggle.IsPending);

        toggle.Press(new PointD(0, 0), 1, onUnit: true, clickCount: 1);
        Assert.False(toggle.Release(new PointD(5, 0), 1.1, moved: false, doubleClickInterval: 0.5));
        toggle.Press(new PointD(0, 0), 2, onUnit: true, clickCount: 1);
        Assert.False(toggle.Release(new PointD(0, 0), 2.6, moved: false, doubleClickInterval: 0.5));
        toggle.Press(new PointD(0, 0), 3, onUnit: true, clickCount: 1);
        Assert.False(toggle.Release(new PointD(0, 0), 3.1, moved: true, doubleClickInterval: 0.5));
        toggle.Press(new PointD(0, 0), 4, onUnit: false, clickCount: 1);
        Assert.False(toggle.Release(new PointD(0, 0), 4.1, moved: false, doubleClickInterval: 0.5));
    }

    [Fact]
    public void TheHitAreaIsAtLeast44By20AndStaysOnTheDisc()
    {
        var center = new PointD(100, 100);
        var hit = OrbUnitToggle.HitRect(OrbReadoutLayout.Led, 148, center, 20);
        Assert.Equal(44, hit.Width, 6);
        Assert.True(hit.Height >= 20);
        Assert.True(hit.Y > center.Y);
        Assert.True(OrbUnitToggle.Contains(hit.Center, hit, center, 148));
        Assert.False(OrbUnitToggle.Contains(new PointD(hit.X - 1, hit.Center.Y), hit, center, 148));

        var minimal = OrbUnitToggle.HitRect(OrbReadoutLayout.Minimal, 72, center, 200);
        Assert.True(minimal.X >= center.X - 36 && minimal.Right <= center.X + 36);
    }

    [Fact]
    public void TheOrbMenuOffersBothUnitsWithTheCurrencySymbol()
    {
        var settings = AppSettings.Default with
        {
            Orbs = new OrbsDocument([new OrbDefinition("o0") { Unit = OrbUnit.Cost }]),
            Cost = CostSettings.Default with { Currency = "TWD" },
        };
        var unit = OrbMenuModel.Build("o0", settings, SourceAvailability.All, En)!.Rows.Single(r => r.Label == En["orbs.unit"]).Submenu!;
        Assert.Equal([En["orbs.unit.tokens"], En.Format("orbs.unit.cost", CurrencyTable.Bundled.Find("TWD")!.SymbolFor("en"))],
            unit.Rows.Select(r => r.Label));
        Assert.Equal([false, true], unit.Rows.Select(r => r.IsChecked));
        Assert.Equal(new MenuAction.SetOrbUnit("o0", OrbUnit.Tokens), unit.Rows.First().Action);
    }

    [Fact]
    public void AddDuplicateAndSplitCopyTheUnitAndMergeKeepsTheTargets()
    {
        var document = new OrbsDocument([new OrbDefinition("a") { Unit = OrbUnit.Cost }, new OrbDefinition("b")]);
        var all = SourceSet.Create([SourceKind.Codex, SourceKind.ClaudeCode]);
        Assert.Equal(OrbUnit.Cost, OrbEditing.Add(document, "a", all, () => "c")!.Document.Find("c")!.Unit);
        Assert.Equal(OrbUnit.Cost, OrbEditing.Duplicate(document, "a", () => "d")!.Document.Find("d")!.Unit);
        Assert.All(OrbEditing.Split(document, "a", all, () => Guid.NewGuid().ToString())!.Document.Orbs.Where(o => o.Id != "b"),
            o => Assert.Equal(OrbUnit.Cost, o.Unit));
        Assert.Equal(OrbUnit.Tokens, OrbEditing.Merge(document, "b")!.Orbs.Single().Unit);
        Assert.Equal(OrbUnit.Tokens, OrbEditing.SetUnit(document, "a", OrbUnit.Tokens).Find("a")!.Unit);
    }

    [Fact]
    public void TheUnitRoundTripsAndUnknownValuesReadAsTokens()
    {
        var settings = AppSettings.Default with { Orbs = new OrbsDocument([new OrbDefinition("a") { Unit = OrbUnit.Cost }]) };
        var json = SettingsSerializer.Serialize(settings);
        Assert.Contains("\"unit\": \"cost\"", json);
        Assert.Equal(OrbUnit.Cost, SettingsSerializer.Deserialize(json).Orbs.Find("a")!.Unit);
        Assert.Equal(OrbUnit.Tokens, SettingsSerializer.Deserialize(json.Replace("\"unit\": \"cost\"", "\"unit\": \"lumens\"")).Orbs.Find("a")!.Unit);
    }

    [Fact]
    public void CostSettingsRoundTripAndAreValidated()
    {
        var settings = AppSettings.Default with
        {
            NetworkEnabled = false,
            Cost = new CostSettings
            {
                Currency = "TWD",
                LiveUnit = LiveUnit.Cost,
                Subscriptions = SourceSet.Create([SourceKind.ClaudeCode, SourceKind.Codex]),
                IncludeSubscriptions = true,
                SyncPrices = false,
                HistoryIncludeSubscriptions = false,
            },
        };
        var back = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(settings));
        Assert.Equal(settings.Cost, back.Cost);
        Assert.False(back.NetworkEnabled);

        var defaults = SettingsSerializer.Deserialize("{}");
        Assert.Equal(CostSettings.Default, defaults.Cost);
        Assert.True(defaults.NetworkEnabled);

        var odd = SettingsSerializer.Deserialize("""{"cost":{"currency":"XXX","liveUnit":"joules","subscriptions":["codex","cursor",5]}}""");
        Assert.Equal(("USD", LiveUnit.Tokens, SourceSet.Of(SourceKind.Codex)), (odd.Cost.Currency, odd.Cost.LiveUnit, odd.Cost.Subscriptions));
        Assert.Equal(SourceSet.Everything.Without(SourceKind.Codex), odd.Cost.Billable);
    }
}
