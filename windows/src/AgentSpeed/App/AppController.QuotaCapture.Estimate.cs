using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgentSpeed.Dashboard.Quota;

namespace AgentSpeed.App;

/// <summary>
/// The developer capture's estimate tooltip (only with <c>--capture-dir</c>): <c>quota-tip</c> (or <c>quota-tip-&lt;n&gt;</c>,
/// the n-th) opens the tooltip of the open dashboard's first quota line with an estimate (DESIGN 4 <i>Tooltip</i>) under the
/// line, and writes <c>&lt;op&gt;.png</c> into the capture folder: the dashboard's content with the tooltip where it shows,
/// at 2× over the theme's backdrop. A tooltip lives in its own popup window, which the <c>dashboard</c> request never sees.
/// </summary>
internal sealed partial class AppController
{
    private const double QuotaTipScale = 2;

    private partial bool RunQuotaEstimateCapture(string op)
    {
        if (op != "quota-tip" && !op.StartsWith("quota-tip-", StringComparison.Ordinal))
        {
            return false;
        }

        var index = op == "quota-tip" ? 1 : Numbered(op, "quota-tip-") ?? 0;
        if (index < 1 || _launch.CaptureDir is not { } folder || _dashboard is not { IsShown: true } dashboard
            || dashboard.Content is not FrameworkElement content)
        {
            return true;
        }

        content.UpdateLayout();
        var panel = Descendants<QuotaLinePanel>(content).Where(p => p.DataContext is QuotaLineRow { HasEstimate: true }).Skip(index - 1).FirstOrDefault();
        if (panel?.ToolTip is not ToolTip tip)
        {
            AppLog.Info($"Quota capture: no estimated line {index} for {op}.");
            return true;
        }

        tip.PlacementTarget = panel;
        tip.Placement = PlacementMode.Bottom;
        tip.VerticalOffset = 2;
        tip.IsOpen = true;
        var path = Path.Combine(folder, op + ".png");
        var dark = _theme.IsDark;
        var delay = new DispatcherTimer(TimeSpan.FromMilliseconds(1200), DispatcherPriority.Background, (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            try
            {
                SaveQuotaTip(content, tip, path, dark);
            }
            finally
            {
                tip.IsOpen = false;
                tip.ClearValue(ToolTip.PlacementTargetProperty);
                tip.ClearValue(ToolTip.PlacementProperty);
                tip.ClearValue(ToolTip.VerticalOffsetProperty);
            }
        }, _ui);
        delay.Start();
        return true;
    }

    /// <summary>The dashboard's content and the tooltip's popup, each rendered in-process and placed as they are on screen.</summary>
    private static void SaveQuotaTip(FrameworkElement content, ToolTip tip, string path, bool dark)
    {
        content.UpdateLayout();
        if (PresentationSource.FromVisual(tip) is not { RootVisual: FrameworkElement popup } || !popup.IsVisible)
        {
            AppLog.Info("Quota capture: the tooltip did not open.");
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(content);
        var origin = content.PointToScreen(new Point(0, 0));
        var at = popup.PointToScreen(new Point(0, 0));
        var tipRect = new Rect((at.X - origin.X) / dpi.DpiScaleX, (at.Y - origin.Y) / dpi.DpiScaleY, popup.ActualWidth, popup.ActualHeight);
        var bounds = Rect.Union(new Rect(0, 0, content.ActualWidth, content.ActualHeight), tipRect);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var backdrop = new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3));
            dc.DrawRectangle(backdrop, null, new Rect(0, 0, bounds.Width, bounds.Height));
            dc.DrawImage(Rendered(content), new Rect(-bounds.X, -bounds.Y, content.ActualWidth, content.ActualHeight));
            dc.DrawImage(Rendered(popup), new Rect(tipRect.X - bounds.X, tipRect.Y - bounds.Y, tipRect.Width, tipRect.Height));
        }

        var target = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width * QuotaTipScale), (int)Math.Ceiling(bounds.Height * QuotaTipScale),
            96 * QuotaTipScale, 96 * QuotaTipScale, PixelFormats.Pbgra32);
        target.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        AppLog.Info($"Captured {Path.GetFileName(path)}");
    }

    private static RenderTargetBitmap Rendered(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * QuotaTipScale), (int)Math.Ceiling(element.ActualHeight * QuotaTipScale),
            96 * QuotaTipScale, 96 * QuotaTipScale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var inner in Descendants<T>(child))
            {
                yield return inner;
            }
        }
    }
}
