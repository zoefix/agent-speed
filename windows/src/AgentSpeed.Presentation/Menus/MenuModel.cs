using System.Collections.Immutable;
using AgentSpeed.Core;

namespace AgentSpeed.Presentation.Menus;

/// <summary>Symbols of the action rows; each platform maps them to its icon font.</summary>
public enum MenuGlyph
{
    Dashboard,
    History,
    Settings,
    About,
    CheckForUpdates,
    Quit,
}

/// <summary>A source's colour dot; an unavailable source is drawn faded.</summary>
public readonly record struct SourceDot(SourceKind Source, bool Available);

/// <summary>What a row shows in the leading icon column.</summary>
public abstract record MenuIcon
{
    private MenuIcon()
    {
    }

    /// <summary>A symbol-font glyph.</summary>
    public sealed record Glyph(MenuGlyph Kind) : MenuIcon;

    /// <summary>The colour dots of one or more sources, packed into the icon box (<see cref="MenuDotLayout"/>).</summary>
    public sealed record Dots(ImmutableArray<SourceDot> Sources) : MenuIcon
    {
        public bool Equals(Dots? other) => other is not null && Sources.SequenceEqual(other.Sources);

        public override int GetHashCode() => Sources.Aggregate(Sources.Length, (hash, dot) => HashCode.Combine(hash, dot));
    }

    /// <summary>The accent dot of an available update.</summary>
    public sealed record AccentDot : MenuIcon;

    /// <summary>Live reporting's small status dot (DESIGN 11, Menu); its meaning is always in the row's text.</summary>
    public sealed record StatusDot(MenuStatusTone Tone) : MenuIcon;
}

/// <summary>The color of a status dot: reporting, quiet, in progress, needs the user; a ring for something switched off.</summary>
public enum MenuStatusTone
{
    Green,
    Gray,
    Amber,
    Red,
    Hollow,
}

/// <summary>A host row of a Hosts submenu (DESIGN 13): its status dot and, while it is not connected, its short word.</summary>
public sealed record MenuHostState(Remote.RemoteDotKind Dot, string? Word)
{
    /// <summary>This computer is always connected; a remote host without a status yet is connecting.</summary>
    public static MenuHostState Default(string hostId) => hostId == Core.Remote.RemoteHost.LocalId
        ? new(Remote.RemoteDotKind.Connected, null)
        : new(Remote.RemoteDotKind.Progress, null);
}

/// <summary>What the quick menu shows of live reporting while it is on and registered (DESIGN 11, Menu).</summary>
/// <param name="ServiceName">The service's name, else its host.</param>
public sealed record ReportMenuState(string ServiceName, bool Paused, bool HasLeaderboard, MenuStatusTone Tone);

/// <summary>A row or a separator of a menu level.</summary>
public abstract record MenuEntry;

public sealed record MenuSeparator : MenuEntry
{
    public static MenuSeparator Instance { get; } = new();
}

/// <summary>
/// A continuous slider row (the size submenu's, DESIGN 2b, Context menu): it runs <see cref="Action"/> with every new
/// stepped value while it moves and shows <see cref="ValueText"/> of it at its trailing edge. <see cref="CheckedRow"/>
/// names the row of the same level that is checked for a value (−1 none), so the presets above follow the slider.
/// </summary>
public sealed record MenuSlider(string Label, double Value, double Minimum, double Maximum, double Step) : MenuEntry
{
    public required Func<double, MenuAction> Action { get; init; }

    public required Func<double, string> ValueText { get; init; }

    public Func<double, int>? CheckedRow { get; init; }

    /// <summary>A requested value on the slider's steps (from its minimum) inside its range.</summary>
    public double Snap(double value) => double.IsFinite(value)
        ? Math.Clamp(Minimum + Math.Round((value - Minimum) / Step) * Step, Minimum, Maximum)
        : Minimum;
}

/// <summary>One row. A row with a <see cref="Submenu"/> opens it; any other enabled row runs its <see cref="Action"/>.</summary>
public sealed record MenuRow(string Label) : MenuEntry
{
    public MenuAction? Action { get; init; }

    public MenuIcon? Icon { get; init; }

    /// <summary>A toggle or a single choice: the platform keeps its toggle semantics and draws the check trailing.</summary>
    public bool IsCheckable { get; init; }

    public bool IsChecked { get; init; }

    public bool IsEnabled { get; init; } = true;

    /// <summary>The non-interactive title row of a menu (the orb's dots and name).</summary>
    public bool IsHeader { get; init; }

    /// <summary>The action of a plain click on the surface that opened the menu (drawn semibold).</summary>
    public bool IsDefault { get; init; }

    /// <summary>Secondary text after the label, for example why a source is unavailable.</summary>
    public string? Detail { get; init; }

    public string? ToolTip { get; init; }

    public MenuLevel? Submenu { get; init; }
}

/// <summary>
/// The rows of one menu or submenu. Every label of a level starts in one text column: the icon column is reserved for
/// the whole level as soon as one row has an icon, and the trailing check / chevron column as soon as one row is
/// checkable or opens a submenu, so checking a row never moves its text (DESIGN 2b, Context menu).
/// </summary>
public sealed record MenuLevel(ImmutableArray<MenuEntry> Entries)
{
    public IEnumerable<MenuRow> Rows => Entries.OfType<MenuRow>();

    public bool ReservesIconColumn => Rows.Any(r => r.Icon is not null);

    public bool ReservesTrailingColumn => Rows.Any(r => r.IsCheckable || r.Submenu is not null);

    public bool Equals(MenuLevel? other) => other is not null && Entries.SequenceEqual(other.Entries);

    public override int GetHashCode() => Entries.Length;
}
