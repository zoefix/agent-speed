using AgentSpeed.Core;
using AgentSpeed.Presentation.Boards;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Settings.Boards;

/// <summary>One leaderboard board (DESIGN 2c, Data model). Immutable; <see cref="Validated"/> brings every field into range.</summary>
public sealed record BoardDefinition
{
    public BoardDefinition(string id)
    {
        Id = id;
    }

    /// <summary>UUID string, never reused.</summary>
    public string Id { get; init; }

    public BoardStyle Style { get; init; } = BoardStyles.Default;

    public BoardSize Size { get; init; } = BoardSize.M;

    /// <summary>As an orb's: every enabled source or an explicit list (METRICS 6.8).</summary>
    public OrbSources Sources { get; init; } = OrbSources.All;

    public double Opacity { get; init; } = OrbDefinition.MaxOpacity;

    public bool AlwaysOnTop { get; init; } = true;

    public bool ClickThrough { get; init; }

    /// <summary>Fade when idle (DESIGN 2, 2c): off for a new board and for one saved before the switch existed.</summary>
    public bool FadeWhenIdle { get; init; }

    /// <summary>macOS only; kept for the round trip.</summary>
    public bool AllSpaces { get; init; } = true;

    public bool Visible { get; init; } = true;

    /// <summary>The live metrics switches (DESIGN 2c, Live metrics): all on for a new board and for one saved without them.</summary>
    public BoardReadouts Readouts { get; init; } = BoardReadouts.All;

    /// <summary>The engine's source set: null tracks every enabled source.</summary>
    public SourceSet? TrackedSources => Sources.Set;

    /// <summary>The hosts it tracks (DESIGN 13); absent in the document = all.</summary>
    public OrbHosts Hosts { get; init; } = OrbHosts.All;

    /// <summary>The engine's host set: null tracks every host.</summary>
    public IReadOnlySet<string>? TrackedHosts => Hosts.HostSet;

    /// <summary>The square's side in DIPs.</summary>
    public double Side => BoardSizes.Side(Size);

    /// <summary>Half the side in physical pixels at a display scale (the "radius" of the shared position records).</summary>
    public double HalfSidePx(double scale) => Side / 2 * scale;

    public BoardDefinition Validated() => this with
    {
        Id = OrbDefinition.CleanId(Id) ?? OrbDefinition.NewId(),
        Style = Enum.IsDefined(Style) ? Style : BoardStyle.Glass,
        Size = Enum.IsDefined(Size) ? Size : BoardSize.M,
        Opacity = OrbDefinition.CleanOpacity(Opacity),
    };
}
