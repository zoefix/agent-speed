using System.Windows;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using AgentSpeed.Core;
using AgentSpeed.Orb.Render;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Orb;

namespace AgentSpeed.Orb;

/// <summary>
/// The instrument of one floating orb, whichever renderer draws it: Direct3D 11 through DirectComposition
/// (<see cref="Gpu.GpuOrbSurface"/>, the default) or the WPF fallback (<see cref="OrbSurface"/>).
/// </summary>
internal interface IOrbSurface
{
    OrbScene Scene { get; }

    double Diameter { get; }

    OrbStyle InstrumentStyle { get; }

    /// <summary>Spoken text (updated at most once per second by the owner).</summary>
    string AccessibleName { get; set; }

    /// <summary>Settings → 懸浮窗 rings this orb while its row is hovered, selected or focused (DESIGN 2b).</summary>
    bool Highlighted { get; set; }

    /// <summary>Pauses rendering (cloaked, occluded, faded out); the first frame after resuming shows the current state.</summary>
    bool Paused { get; set; }

    /// <summary>The session is locked or the console disconnected: no frames until it comes back.</summary>
    bool Locked { get; set; }

    bool IsPreferred { get; set; }

    void Configure(OrbSurfaceConfig config);

    void SetIdentity(IReadOnlyList<SourceKind>? sources, SourceSet unavailable);

    /// <param name="unavailableTitle">Every source is unavailable: the value reads an en dash and this title.</param>
    void SetData(double value, SpeedTier tier, SpeedTitle title, IReadOnlyList<SourceKind> generating, string infoLine, string? unavailableTitle);

    void SetHover(bool hovering);

    /// <summary>
    /// One sample for the two-line readout (DESIGN 10): <paramref name="enabled"/> is the orb's readout setting where it can
    /// show, <paramref name="sample"/> what it shows (null: nothing to show).
    /// </summary>
    void SetLines(Presentation.Orb.Lines.OrbLinesSample? sample, bool enabled);

    /// <summary>The <c>character</c> style's character and cards (DESIGN 2a.6); other styles ignore it.</summary>
    void SetCharacter(Presentation.Orb.OrbCharacterContent? content);

    /// <summary>
    /// One engine tick of the <c>character</c> cards' values (性能 and 思考, the cost rate, the 首字 latency, the running
    /// models).
    /// </summary>
    void SetCharacterReadings(Presentation.Orb.Lines.OrbLinesSample? performance, Presentation.Cost.LiveMoney? money, bool generating,
        Presentation.Orb.OrbLatency? latency, IReadOnlyList<Presentation.Orb.OrbModelRow> models);

    /// <summary>One traffic sample of the <c>character</c> network card (null while not measured).</summary>
    void SetCharacterNetwork(Presentation.Orb.Lines.OrbLinesSample? network);

    /// <summary>The character cards' spoken text (DESIGN 2a.6, Accessibility), or null.</summary>
    string? CharacterSentence(Presentation.Localization.Localizer localizer, string currencyName);

    /// <summary>The two-line readout is visible now (the accessible label and the tooltip read it).</summary>
    bool LinesVisible { get; }

    /// <summary>What the two-line readout shows, or null.</summary>
    Presentation.Orb.Lines.OrbLinesReading? LinesReading { get; }

    void Pulse(double speed, long outputTokens);

    void Peak();
}

/// <summary>One floating orb window (DESIGN 2, 2b), whichever renderer draws it.</summary>
internal interface IOrbWindow
{
    string OrbId { get; }

    IOrbSurface Surface { get; }

    nint Handle { get; }

    /// <summary>Disc center in physical pixels.</summary>
    PointD Center { get; }

    bool HasCenter { get; }

    bool IsDragging { get; }

    bool IsHovered { get; }

    /// <summary>The disc center as drawn: the window's whole-pixel origin plus half its size (physical px).</summary>
    PointD DrawnCenter { get; }

    /// <summary>The display scale (dpi / 96) at the disc center.</summary>
    double DisplayScale { get; }

    OrbWindowOptions Options { get; }

    bool IsVisible { get; }

    /// <summary>The window's opacity (the unavailable dim and the idle fade); name plates follow it.</summary>
    double Opacity { get; }

    /// <summary>Raised when <see cref="Opacity"/> changes (every frame of a fade).</summary>
    event EventHandler? OpacityChanged;

    event EventHandler? DashboardRequested;

    event EventHandler? MenuRequested;

    event EventHandler? Interacted;

    event EventHandler<bool>? HoverChanged;

    event EventHandler<PointD>? DragReleased;

    event EventHandler? DragMoved;

    Func<PointD, PointD>? DragAssist { get; set; }

    void SetDimmed(bool dimmed);

    void Attach(IOrbAttachment attachment);

    void Detach(IOrbAttachment attachment);

    void SetPlateHover(bool hovering);

    void PlatePress(int clickCount);

    void PlateMove();

    void PlateRelease();

    void PlateMenu();

    void Configure(OrbSurfaceConfig config, OrbWindowOptions options);

    void SetActions(IReadOnlyList<(string Name, Action Invoke)> actions);

    void SetLanguage(XmlLanguage language);

    /// <summary>The tooltip of the unit's hit area (<c>orbs.unit.toggleHint</c>, DESIGN 9 Switching).</summary>
    void SetUnitHint(string hint);

    /// <summary>The tooltip of the rest of the disc (the intelligence readout's lines, DESIGN 10); null for none.</summary>
    void SetDiscHint(string? hint);

    /// <summary>A click on the unit toggled it (after the double-click interval): the owner switches the orb's unit.</summary>
    event EventHandler? UnitToggleRequested;

    /// <summary>Reads a short text once through UI Automation (the new unit after a switch).</summary>
    void Announce(string text);

    /// <summary>Makes <paramref name="plate"/> an owned window of the orb (it stays in front of it).</summary>
    void OwnPlate(Window plate);

    void MoveTo(PointD center);

    void AnimateTo(PointD center, double response = 0.35, double damping = 0.8, Action? done = null);

    void Appear();

    void Vanish(Action done, double seconds = 0.2);

    void NoteActivity(bool active);

    void BringToFront();

    void Show();

    void Close();

    /// <summary>The orb as drawn now, premultiplied BGRA at the window's pixel size (developer capture), or null.</summary>
    BitmapSource? Snapshot();
}
