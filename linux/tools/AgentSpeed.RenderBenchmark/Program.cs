using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using AgentSpeed.Linux.Rendering;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Settings;
using SkiaSharp;

[assembly: SupportedOSPlatform("linux")]

if (args is ["--capture", var directory]) { OrbGallery.Capture(directory); return; }

var results = new List<object>();
foreach (var count in new[] { 1, 5 })
foreach (var active in new[] { false, true })
{
    var glyphs = Enumerable.Range(0, count).Select(_ => new SkiaGlyphs()).ToArray();
    var renderers = glyphs.Select(g => new SkiaOrbRenderer(g)).ToArray();
    var orbs = glyphs.Select((g, i) => new OrbGpuOrb((uint)(42 + i), g)).ToArray();
    var effects = new EffectsPolicy(EffectsIntensity.Standard);
    for (var i = 0; i < count; i++)
        orbs[i].Configure(new(OrbStyles.All[i], 148, effects, true, true, 100,
            new("tok/s", "NEW PEAK", Enumerable.Repeat("BENCHMARK", 14).ToArray(), true), null, default), new(148, 1, OrbTokens.Dark, effects));
    using var surface = SKSurface.Create(new SKImageInfo(222, 222));
    void Frame(int index)
    {
        var now = index / 30d;
        for (var i = 0; i < count; i++)
        {
            var value = active ? 110 + Math.Sin(index * .1) * 25 : 0;
            glyphs[i].BeginFrame();
            orbs[i].SetData(now, value, HeatMath.RawTier(value, 100), SpeedTitles.Raw(value, 100), [], "", null);
            orbs[i].Render(now); surface.Canvas.Clear(SKColors.Transparent); renderers[i].Draw(surface.Canvas, orbs[i].List, software: true);
        }
    }
    for (var i = 0; i < 15; i++) Frame(i);
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    var memory = GC.GetTotalMemory(true);
    var allocations = GC.GetAllocatedBytesForCurrentThread();
    var clock = Stopwatch.StartNew();
    for (var i = 15; i < 75; i++) Frame(i);
    clock.Stop();
    var allocated = GC.GetAllocatedBytesForCurrentThread() - allocations;
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    results.Add(new { orbs = count, active, renderer = "Skia CPU (no display required)",
        millisecondsPerCombinedFrame = clock.Elapsed.TotalMilliseconds / 60,
        allocatedBytesPerCombinedFrame = allocated / 60, retainedManagedBytes = GC.GetTotalMemory(true) - memory });
    foreach (var renderer in renderers) renderer.Dispose();
    foreach (var font in glyphs) font.Dispose();
}
Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
