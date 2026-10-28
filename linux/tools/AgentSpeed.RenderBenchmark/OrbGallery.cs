using System.Text.Json;
using AgentSpeed.Linux.Rendering;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Titles;
using SkiaSharp;

internal static class OrbGallery
{
    internal static void Capture(string directory)
    {
        Directory.CreateDirectory(directory);
        var loc = new Localizer(Localizer.LoadEmbeddedTable(), "zh-Hans");
        var labels = TitleThemesDocument.Default.General.Labels(loc);
        foreach (var tokens in new[] { OrbTokens.Dark, OrbTokens.Light })
        foreach (var style in OrbStyles.All)
        foreach (var speed in new[] { 0d, 183d })
        {
            using var glyphs = new SkiaGlyphs(); using var renderer = new SkiaOrbRenderer(glyphs);
            var orb = new OrbGpuOrb(42, glyphs); var effects = new EffectsPolicy(EffectsIntensity.Standard);
            const double redline = HeatMath.ReferenceRedline;
            orb.Configure(new(style, 148, effects, true, true, redline, new("tok/s", loc["peak.new"], labels, false), null, default),
                new(148, 1, tokens, effects));
            orb.SetData(10, speed, HeatMath.RawTier(speed, redline), SpeedTitles.Raw(speed, redline), [], "", null);
            // Capture the settled instrument, after the odometer and entry flash.
            for (var frame = 0; frame <= 90; frame++) orb.Render(10 + frame / 30d);
            foreach (var cpu in new[] { false, true })
            {
                using var surface = SKSurface.Create(new SKImageInfo(444, 444));
                surface.Canvas.Clear(SKColors.Transparent); surface.Canvas.Scale(2);
                renderer.Draw(surface.Canvas, orb.List, cpu);
                using var image = surface.Snapshot(); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var output = File.Create(Path.Combine(directory, $"{style}-{tokens.Appearance}-{speed}-{(cpu ? "cpu" : "shader")}.png"));
                data.SaveTo(output);
            }
            File.WriteAllText(Path.Combine(directory, $"{style}-{tokens.Appearance}-{speed}.json"), JsonSerializer.Serialize(
                orb.List.Texts.ToArray().Select(t => new { text = glyphs.At(t.Uv.X).Key, rect = new[] { t.Rect.X, t.Rect.Y, t.Rect.Z, t.Rect.W },
                    color = new[] { t.Color.X, t.Color.Y, t.Color.Z, t.Color.W }, clip = new[] { t.Clip.X, t.Clip.Y, t.Clip.Z, t.Clip.W } }),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        CaptureOriginalStyles(Path.Combine(directory, "original-styles-dark.png"), loc, labels);
    }

    private static void CaptureOriginalStyles(string file, Localizer loc, IReadOnlyList<string> labels)
    {
        using var sheet = SKSurface.Create(new SKImageInfo(444 * OrbStyles.All.Count, 444));
        sheet.Canvas.Clear(new SKColor(32, 33, 38));
        for (var i = 0; i < OrbStyles.All.Count; i++)
        {
            using var glyphs = new SkiaGlyphs(); using var renderer = new SkiaOrbRenderer(glyphs);
            var effects = new EffectsPolicy(EffectsIntensity.Standard); var orb = new OrbGpuOrb(42, glyphs);
            orb.Configure(new(OrbStyles.All[i], 148, effects, true, true, HeatMath.ReferenceRedline,
                new("tok/s", loc["peak.new"], labels, false), null, default), new(148, 1, OrbTokens.Dark, effects));
            orb.SetData(10, 183, HeatMath.RawTier(183, HeatMath.ReferenceRedline), SpeedTitles.Raw(183, HeatMath.ReferenceRedline), [], "", null);
            for (var frame = 0; frame <= 90; frame++) orb.Render(10 + frame / 30d);
            sheet.Canvas.Save(); sheet.Canvas.Translate(i * 444, 0); sheet.Canvas.Scale(2);
            renderer.Draw(sheet.Canvas, orb.List); sheet.Canvas.Restore();
        }
        using var image = sheet.Snapshot(); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var output = File.Create(file); png.SaveTo(output);
    }
}
