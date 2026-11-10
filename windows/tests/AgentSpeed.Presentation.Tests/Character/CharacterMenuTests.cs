using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Menus;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Tests.Character;

/// <summary>DESIGN 2b, Context menu items 2–3: the character submenu and the cards in place of the unit and traffic items.</summary>
public class CharacterMenuTests
{
    private const string Separator = "—";

    private static readonly Localizer En = new(Localizer.LoadEmbeddedTable(), LanguageCodes.En);

    private static readonly MenuCharacter[] Installed = [new("claude", "Claude-chan"), new("kimi", "Kimi-chan")];

    private static AppSettings With(OrbDefinition orb, bool traffic = true) =>
        AppSettings.Default with { Orbs = new OrbsDocument([orb]), Traffic = AppSettings.Default.Traffic with { Enabled = traffic } };

    private static string[] Outline(MenuLevel level) =>
        [.. level.Entries.Select(e => e switch { MenuRow row => row.Label, MenuSlider slider => "slider " + slider.Label, _ => Separator })];

    private static MenuRow Row(MenuLevel level, string key) => level.Rows.Single(r => r.Label == En[key]);

    [Fact]
    public void ACharacterOrbHasTheCharacterAndReadoutItems()
    {
        var orb = new OrbDefinition("o0") { Style = OrbStyle.Character, Character = "kimi" };
        var menu = OrbMenuModel.Build("o0", With(orb), SourceAvailability.All, En, characters: Installed)!;
        var outline = Outline(menu);
        Assert.Equal(En["characters.title"], outline[Array.IndexOf(outline, En["orbs.style"]) + 1]);
        Assert.Contains(En["character.readouts"], outline);
        Assert.DoesNotContain(En["orbs.unit"], outline);
        Assert.DoesNotContain(En["orbs.traffic"], outline);

        var characters = Row(menu, "characters.title").Submenu!;
        Assert.Equal([En["characters.auto"], Separator, "Claude-chan", "Kimi-chan", Separator, En["characters.more"]], Outline(characters));
        Assert.True(characters.Rows.Single(r => r.Label == "Kimi-chan").IsChecked);
        Assert.False(characters.Rows.Single(r => r.Label == En["characters.auto"]).IsChecked);
        Assert.Equal(new MenuAction.SetOrbCharacter("o0", "claude"), characters.Rows.Single(r => r.Label == "Claude-chan").Action);
    }

    [Fact]
    public void TheReadoutsMenuChecksEachCardAndTheNetworkScope()
    {
        var orb = new OrbDefinition("o0") { Style = OrbStyle.Character, Traffic = OrbTraffic.Machine };
        var readouts = Row(OrbMenuModel.Build("o0", With(orb), SourceAvailability.All, En)!, "character.readouts").Submenu!;
        Assert.Equal(
            [
                En["character.readouts.intel"], En["character.readouts.thinking"], En["character.readouts.latency"], En["character.readouts.network"],
                En["character.readouts.cost"], En["character.readouts.agents"], En["character.readouts.models"], En["character.readouts.quota"],
                En["character.readouts.model"],
                Separator, En["character.readouts.networkScope"],
                En["orbs.traffic.sources"], En["orbs.traffic.machine"],
            ],
            Outline(readouts));
        Assert.False(readouts.Rows.Single(r => r.Label == En["character.readouts.model"]).IsChecked);
        Assert.True(readouts.Rows.Single(r => r.Label == En["orbs.traffic.machine"]).IsChecked);
        Assert.Equal(new MenuAction.SetOrbCharacterReadout("o0", CharacterReadout.Model, true),
            readouts.Rows.Single(r => r.Label == En["character.readouts.model"]).Action);

        var off = Row(OrbMenuModel.Build("o0", With(orb, traffic: false), SourceAvailability.All, En)!, "character.readouts").Submenu!;
        Assert.False(off.Rows.Single(r => r.Label == En["character.readouts.network"]).IsEnabled);
        Assert.False(off.Rows.Single(r => r.Label == En["orbs.traffic.machine"]).IsEnabled);
    }

    [Fact]
    public void TheQuickMenuHasTheSameItems()
    {
        var orb = new OrbDefinition("o0") { Style = OrbStyle.Character };
        var settings = AppSettings.Default with { Orbs = new OrbsDocument([orb, new OrbDefinition("o1")]) };
        var quick = QuickMenuModel.Build(settings, SourceAvailability.All, En, characters: Installed);
        var orbs = quick.Rows.Single(r => r.Label == En["settings.orb"]).Submenu!;
        var first = orbs.Rows.First().Submenu!;
        Assert.Contains(En["characters.title"], Outline(first));
        Assert.Contains(En["character.readouts"], Outline(first));
        Assert.DoesNotContain(En["orbs.unit"], Outline(first));
    }
}
