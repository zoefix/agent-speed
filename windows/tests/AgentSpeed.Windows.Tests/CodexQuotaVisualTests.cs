using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgentSpeed.Interop;
using AgentSpeed.Orb;
using AgentSpeed.Orb.Names;
using AgentSpeed.Orb.Render;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Orb.Names;
using AgentSpeed.Presentation.Quota;

namespace AgentSpeed.Windows.Tests;

public sealed class CodexQuotaVisualTests
{
    [Fact]
    public void NativeCapsulesFitAt112PixelsAndRenderInBothThemes() => OnSta(() =>
    {
        var visual = new DrawingVisual();
        var type = new PlateTypography("zh-Hans");
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x15, 0x18, 0x20)), null, new Rect(0, 0, 240, 144));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF0, 0xF2, 0xF6)), null, new Rect(240, 0, 240, 144));
            for (var theme = 0; theme < 2; theme++)
            {
                var dark = theme == 0;
                var row = 0;
                foreach (var remaining in new[] { 46, 20, 5 })
                {
                    var metrics = new NamePlateMetrics(112);
                    var layout = Layout(remaining, metrics, type);
                    Assert.InRange(layout.Width, 1, metrics.AloneBudget);
                    var ink = new PlateInk(new PlateLook(dark, false), CodexQuotaDisplay.Color(CodexQuotaDisplay.SeverityOf(remaining), dark));
                    dc.PushTransform(new TranslateTransform(theme * 240 + (240 - layout.Width) / 2, 18 + row++ * 42));
                    PlatePainter.Body(dc, new Rect(0, 0, layout.Width, metrics.Height), ink);
                    PlatePainter.Content(dc, layout, ink, type, 2);
                    dc.Pop();
                }
            }
        }

        var bitmap = new RenderTargetBitmap(960, 288, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(AppContext.BaseDirectory, "codex-quota-colors.png"));
        png.Save(file);
    });

    [Fact]
    public void AttachedCapsuleFollowsTheOrbsNativeMoveBatch() => OnSta(() =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var clock = new OrbClock(dispatcher, () => 1);
        var orb = new OrbWindow("quota-test", () => 1, clock, 1) { Opacity = 0 };
        OrbPlate? plate = null;
        try
        {
            orb.Show();
            orb.MoveTo(new PointD(-10000, -10000));
            var window = new NamePlateWindow(orb);
            plate = new OrbPlate(orb, window, () => 1);
            orb.Attach(plate);
            var type = new PlateTypography("zh-Hans");
            var metrics = new NamePlateMetrics(112);
            var layout = Layout(46, metrics, type);
            plate.SetStyle(new PlateInk(new(true, false)), type, true, 1);
            plate.Show(layout, null, NamePlateSlot.Below, new Rect(-layout.Width / 2, 62, layout.Width, metrics.Height), new Rect(-500, -500, 1000, 1000));
            Assert.True(User32.GetWindowRect(plate.Handle, out var before));
            orb.MoveTo(new PointD(-9880, -9940));
            Assert.True(User32.GetWindowRect(plate.Handle, out var after));
            Assert.Equal(120, after.Left - before.Left);
            Assert.Equal(60, after.Top - before.Top);
        }
        finally
        {
            plate?.Close();
            orb.Close();
            dispatcher.InvokeShutdown();
        }
    });

    private static NamePlateLayout Layout(int remaining, NamePlateMetrics metrics, PlateTypography type) => NamePlateLayout.Of(
        new NamePlateLine([new([], "Codex 剩余", NamePlateInk.Primary, true), new([], $"{remaining}%", NamePlateInk.Accent, false)]), metrics, type.Measure);

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) { throw new InvalidOperationException("Quota visual check failed.", failure); }
    }
}
