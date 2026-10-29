using AgentSpeed.Presentation.Colors;

namespace AgentSpeed.Presentation.Boards.Scene;

/// <summary>
/// The sub-gauge's tokens per style and appearance (DESIGN 2c, Live metrics: Sub-gauge): glass's pearl arc and its track,
/// timing's data box, neon's LED digits, ghost segments and meter. Windows high contrast keeps no glows or ghosts: numbers
/// in the text color, the arc and lit segments full strength, labels, units and quiet values (a resting arc or segment too)
/// in the dimmer ink, the track and unlit segments in the ink-3 token below it, no fills.
/// </summary>
/// <param name="Number">Numbers (glass, timing: ink; neon: <c>led</c>).</param>
/// <param name="Quiet">Labels, units and quiet values (<c>–</c>, <c>0</c>, the tag): <c>ink-3</c>.</param>
/// <param name="Track">Glass's arc track (<c>line-2</c>).</param>
/// <param name="PearlA">Glass: the arc's gradient, lower left <see cref="PearlC"/> → <see cref="PearlA"/> → <see cref="PearlB"/> upper right.</param>
/// <param name="Box">Timing's box fill (<c>slab-2</c>).</param>
/// <param name="BoxLine">Timing's inner stroke and separators (<c>line-2</c>).</param>
/// <param name="Tab">Timing's tab column (<c>block</c>).</param>
/// <param name="TabInk">Timing's small tab labels (<c>ink-2</c>).</param>
/// <param name="TabMainInk">Timing's 性能 tab label (<c>block-ink</c>).</param>
/// <param name="Ghost">Neon's ghost <c>8</c>s.</param>
/// <param name="Mind">Neon's lit meter segments.</param>
/// <param name="Unlit">Neon's unlit meter segments.</param>
/// <param name="Glows">Glows are drawn (neon dark and glass's arc; never in high contrast).</param>
public sealed record BoardGaugePalette(
    Rgba Number,
    Rgba Quiet,
    Rgba Track,
    Rgba PearlA,
    Rgba PearlB,
    Rgba PearlC,
    Rgba Box,
    Rgba BoxLine,
    Rgba Tab,
    Rgba TabInk,
    Rgba TabMainInk,
    Rgba Ghost,
    Rgba Mind,
    Rgba Unlit,
    bool Glows)
{
    /// <summary>The rows' 性能 cell while live (neon's <c>led</c> at 78 %).</summary>
    public Rgba RowLive { get; init; }

    /// <summary>The rows' 性能 cell while resting or none.</summary>
    public Rgba RowQuiet { get; init; }

    /// <summary>The dark appearance (glass's tip is white with a glow on dark, <c>pearl-b</c> without one on light).</summary>
    public bool Dark { get; init; }

    /// <summary>Windows high contrast: solid inks, the provisional mark's dots at 100 % and thicker, a resting look in the quiet ink.</summary>
    public bool HighContrast { get; init; }

    public static BoardGaugePalette For(BoardStyle style, BoardPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        var dark = palette.Dark;
        if (palette.HighContrast)
        {
            // The track and the unlit segments keep the ink-3 token itself, under the strengthened quiet ink that a resting arc
            // or segment takes, so values, resting values and the provisional mark's dots all stand apart from them (DESIGN 12,
            // Platform details: Board under Increase Contrast).
            var ink = palette.Ink;
            var dim = palette.Ink3;
            var track = ink.WithAlpha(BoardPalette.For(style, dark).Ink3.A);
            return new BoardGaugePalette(ink, dim, track, ink, ink, ink, default, dim, default, dim, ink, default, ink, track, false)
            {
                Dark = dark,
                HighContrast = true,
                RowLive = ink,
                RowQuiet = dim,
            };
        }

        switch (style)
        {
            case BoardStyle.Timing:
                return new BoardGaugePalette(palette.Ink, palette.Ink3, default, default, default, default, palette.Panel2,
                    dark ? Rgba.White.WithAlpha(0.10) : Rgba.Black.WithAlpha(0.10), palette.Block, palette.Ink2, palette.BlockInk, default, default,
                    default, false)
                {
                    Dark = dark,
                    RowLive = palette.Ink2,
                    RowQuiet = palette.Ink3,
                };
            case BoardStyle.Neon:
            {
                var led = Rgba.FromHex(dark ? "#EAF2FF" : "#141A33");
                return new BoardGaugePalette(led, dark ? new Rgba(214 / 255.0, 224 / 255.0, 1, 0.36) : new Rgba(10 / 255.0, 13 / 255.0, 24 / 255.0, 0.40),
                    default, default, default, default, default, default, default, default, default,
                    dark ? new Rgba(160 / 255.0, 190 / 255.0, 1, 0.075) : new Rgba(20 / 255.0, 30 / 255.0, 80 / 255.0, 0.06),
                    Rgba.FromHex(dark ? "#CFE0FF" : "#3D4F9A"),
                    dark ? new Rgba(140 / 255.0, 170 / 255.0, 1, 0.16) : new Rgba(20 / 255.0, 30 / 255.0, 80 / 255.0, 0.14), dark)
                {
                    Dark = dark,
                    RowLive = led.MultiplyAlpha(0.78),
                    RowQuiet = palette.Ink3,
                };
            }

            default:
                return new BoardGaugePalette(
                    dark ? new Rgba(244 / 255.0, 246 / 255.0, 252 / 255.0, 0.96) : new Rgba(17 / 255.0, 20 / 255.0, 30 / 255.0, 0.94),
                    dark ? new Rgba(226 / 255.0, 230 / 255.0, 242 / 255.0, 0.38) : new Rgba(30 / 255.0, 36 / 255.0, 52 / 255.0, 0.40),
                    dark ? Rgba.White.WithAlpha(0.14) : Rgba.Black.WithAlpha(0.11),
                    Rgba.FromHex(dark ? "#C7D2FE" : "#6D7CF0"), Rgba.FromHex(dark ? "#F5D0FE" : "#C05BD9"), Rgba.FromHex(dark ? "#A5F3FC" : "#1AA3C4"),
                    default, default, default, default, default, default, default, default, true)
                {
                    Dark = dark,
                    RowLive = palette.Ink2,
                    RowQuiet = palette.Ink3,
                };
        }
    }
}
