using AgentSpeed.Presentation.Boards;
using AgentSpeed.Presentation.Boards.Scene;
using Vortice.Direct2D1;

namespace AgentSpeed.Boards.Layers;

/// <summary>The cached layers of a board (DESIGN 2c, Two layers).</summary>
internal enum BoardLayerKind
{
    /// <summary>A row without its value: rank, badge, project, model, capsule, share.</summary>
    RowBody,

    /// <summary>The static parts by a row's value (timing's fastest dot and rule, neon's lead tint); none when there are none.</summary>
    RowValue,

    /// <summary>The hero without its number, unit and history: title and counts (timing: its live label).</summary>
    Hero,

    /// <summary>The number's unit (<c>BoardHeroLayout.UnitPad</c> in from the layer's left, the hero's height).</summary>
    Unit,

    /// <summary>The hero's history: glass's sparkline, timing's bars, neon's LED bar (<c>BoardHeroLayout.HistoryPad</c> around it).</summary>
    History,

    Footer,

    /// <summary>The static pattern behind the content (timing's hatch and footer slab, neon's grid); the whole square.</summary>
    Backdrop,

    /// <summary>One glyph of the hero number (<see cref="BoardLayerRequest.Glyph"/>), padded by <c>BoardHeroNumber.GlyphPad</c>.</summary>
    Glyph,

    /// <summary>A short-lived callout (<see cref="BoardLayerRequest.Callout"/>): a chip, timing's block or banner.</summary>
    Callout,

    /// <summary>
    /// One white glyph (<see cref="BoardLayerRequest.Glyph"/>) of the rows' values or timing's intervals
    /// (<see cref="BoardLayerRequest.Set"/>), padded by <c>BoardValueGlyphs.Pad</c>.
    /// </summary>
    ValueGlyph,

    /// <summary>A generating badge of a source (<see cref="BoardLayerRequest.Row"/> tells which), pulsed by the scene (<c>BoardPulses</c>).</summary>
    Badge,

    /// <summary>A text of the hero's sub-gauge (<see cref="BoardLayerRequest.Part"/>): a label, a unit, <c>≥</c> or the tag, white.</summary>
    GaugeText,

    /// <summary>The captions line at the top of the ranking (性能, tok/s, timing's 間隔 and the hairline).</summary>
    Captions,
}

/// <summary>Which layer a painter describes or draws: the row, glyph or callout it is for, and the scene's current tick.</summary>
internal readonly record struct BoardLayerRequest(BoardLayerKind Kind, BoardScene Scene, BoardRowMotion? Row, char Glyph = '\0',
    BoardCallout? Callout = null, BoardValueSet Set = BoardValueSet.Group, BoardGaugePart Part = BoardGaugePart.LabelIntelligence);

/// <summary>
/// A layer's size in points and its content identity: an unchanged identity is never rasterized again. <paramref name="MeterX"/>
/// marks a row's top-rung meter for the scene's shimmer (part of what the signature identifies); <paramref name="Baseline"/> a text
/// layer's baseline from its top (the scene sets text on baselines).
/// </summary>
internal readonly record struct BoardLayerSpec(double Width, double Height, string Signature, double MeterX = double.NaN, double Baseline = double.NaN);

/// <summary>
/// Draws a board's layers with Direct2D and DirectWrite, only when their content changes: the styles' painters
/// (<see cref="BoardPainters.Create"/>) or the neutral <see cref="PlaceholderBoardPainter"/>.
/// </summary>
internal interface IBoardLayerPainter
{
    /// <summary>The layer's size and identity, or null when the board shows no such layer.</summary>
    BoardLayerSpec? Describe(in BoardLayerRequest request);

    /// <summary>Draws the layer in points from its top-left corner; the box is already clear.</summary>
    void Paint(ID2D1DeviceContext context, in BoardLayerRequest request, in BoardLayerSpec spec);

    /// <summary>
    /// The two-line group rows' figures measured in the style's fonts at the scene's size (DESIGN 2c, The second line): the widest
    /// 性能 and value at their new sizes, and the cap heights that centre them on the row.
    /// </summary>
    BoardRowFigures Figures(BoardScene scene);
}
