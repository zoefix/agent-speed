using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgentSpeed.App;
using AgentSpeed.Orb.Hero.Badge;

namespace AgentSpeed.Services;

/// <summary>
/// Developer option <c>--capture-dir</c>: renders app visuals (the floating orbs with their name plates and chips at
/// their screen positions over a backdrop, or a menu) into PNG files when a <c>capture.request</c> file appears in that
/// folder. Rendering in-process means no screen capture and nothing of other windows is ever recorded. Request lines:
/// <c>orbs name [#backdrop | @C:\path\to\image.png]</c> (an image covers the captured area, cropped to fill),
/// <c>menu-tray name</c>, <c>menu-orb name</c>, <c>badge-sheet name [dark|light]</c> (the hero title badge of every theme
/// but Vehicles at idle and levels 1–13), <c>badge-strip name [dark|light]</c> (its transitions at fixed τ),
/// <c>dashboard name [#backdrop]</c> (the open dashboard's content) and <c>taskbar name [#backdrop] [dark|light] [2x]</c>
/// (the widget as it draws now, rendered again in-process, optionally on a dark or light taskbar and at a given scale),
/// <c>perfcard name [#backdrop] [2x]</c> and <c>perfhistory name [#backdrop] [2x]</c> (the performance score's sample sheets).
/// </summary>
internal sealed partial class DevCapture
{
    private readonly string _folder;
    private readonly DispatcherTimer _poll;
    private const long MaxBackdropBytes = 64L * 1024 * 1024;

    private readonly Func<IReadOnlyList<(FrameworkElement Visual, Rect ScreenRect, double Opacity)>> _orbs;
    private readonly Func<string, ContextMenu?> _menu;
    private readonly Func<string, FrameworkElement?> _element;

    /// <summary>Runs a named orb operation (<c>add</c>, <c>split</c>, <c>merge</c>, <c>undo</c>, <c>corner</c>) for flow checks.</summary>
    public Action<string>? Operation { get; init; }

    /// <summary>The title themes and language for <c>badge-sheet</c> and <c>badge-strip</c>.</summary>
    public Func<BadgeCaptureSource>? Badges { get; init; }

    /// <summary>
    /// The taskbar widget rendered now (<c>taskbar</c>), or null: on a dark (true) or light (false) taskbar, else the
    /// system's, at a scale (pixels per DIP), else the primary monitor's.
    /// </summary>
    public Func<bool?, double?, BitmapSource?>? Taskbar { get; init; }

    public DevCapture(Dispatcher dispatcher, string folder, Func<IReadOnlyList<(FrameworkElement Visual, Rect ScreenRect, double Opacity)>> orbs,
        Func<string, ContextMenu?> menu, Func<string, FrameworkElement?> element)
    {
        _folder = folder;
        _orbs = orbs;
        _menu = menu;
        _element = element;
        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => Poll(), dispatcher);
        _poll.Start();
    }

    [GeneratedRegex("^[a-z0-9-]{1,48}$")]
    private static partial Regex SafeName();

    private void Poll()
    {
        var request = Path.Combine(_folder, "capture.request");
        string text;
        try
        {
            if (!File.Exists(request))
            {
                return;
            }

            text = File.ReadAllText(request).Trim();
            File.Delete(request);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        var parts = text.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !SafeName().IsMatch(parts[1]))
        {
            return;
        }

        try
        {
            switch (parts[0])
            {
                case "orbs":
                    CaptureOrbs(parts[1], parts.Length > 2 ? parts[2] : "#202020");
                    break;
                case "menu-tray" or "menu-orb":
                    CaptureMenu(parts[0], parts[1]);
                    break;
                case "op":
                    Operation?.Invoke(parts[1]);
                    break;
                case "settings":
                    CaptureElement(parts[1], parts.Length > 2 ? parts[2] : "#202020");
                    break;
                case "dashboard" or "history" or "report-consent" or "report-preview" or "report-invite":
                    CaptureElement(parts[1], parts.Length > 2 ? parts[2] : "#202020", parts[0]);
                    break;
                case "taskbar":
                    var options = parts.Length > 2 ? parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
                    var theme = options.FirstOrDefault(o => o is "dark" or "light");
                    var scale = options.FirstOrDefault(o => o is "1x" or "2x" or "3x") is { } factor ? factor[0] - '0' : (double?)null;
                    if (Taskbar?.Invoke(theme is null ? null : theme == "dark", scale) is { } bar)
                    {
                        Save(OnBackdrop(bar, options.FirstOrDefault(o => o.StartsWith('#')) ?? "#202020"), parts[1]);
                    }

                    break;
                case "perfcard" or "perfhistory":
                    CapturePerf(parts[0], parts[1], parts.Length > 2 ? parts[2] : string.Empty);
                    break;
                case "badge-sheet" or "badge-strip" when Badges?.Invoke() is { } source:
                    var dark = !(parts.Length > 2 && string.Equals(parts[2], "light", StringComparison.OrdinalIgnoreCase));
                    Save(parts[0] == "badge-sheet" ? BadgeCapture.Sheet(source, dark) : BadgeCapture.Strip(source, dark), parts[1]);
                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or NotSupportedException
            or FormatException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            AppLog.Warn("Capture failed.", ex);
        }
    }

    private void CaptureOrbs(string name, string backdrop)
    {
        var orbs = _orbs();
        if (orbs.Count == 0)
        {
            return;
        }

        var left = orbs.Min(o => o.ScreenRect.Left);
        var top = orbs.Min(o => o.ScreenRect.Top);
        var right = orbs.Max(o => o.ScreenRect.Right);
        var bottom = orbs.Max(o => o.ScreenRect.Bottom);
        var area = new Rect(0, 0, right - left, bottom - top);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (backdrop.StartsWith('@'))
            {
                DrawCover(dc, LoadBackdrop(backdrop[1..].Trim().Trim('"'), area), area);
            }
            else
            {
                dc.DrawRectangle(new SolidColorBrush((Color)ColorConverter.ConvertFromString(backdrop)), null, area);
            }

            foreach (var (element, rect, opacity) in orbs)
            {
                if (element is Orb.OrbSnapshotElement orb)
                {
                    if (orb.Snapshot() is { } snapshot)
                    {
                        dc.PushOpacity(orb.IncludesOpacity ? 1 : Math.Clamp(opacity, 0, 1));
                        dc.DrawImage(snapshot, new Rect(rect.Left - left, rect.Top - top, rect.Width, rect.Height));
                        dc.Pop();
                    }

                    continue;
                }

                var scale = rect.Width / Math.Max(1, element.ActualWidth);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(rect.Width), (int)Math.Ceiling(rect.Height), 96 * scale, 96 * scale,
                    PixelFormats.Pbgra32);
                bitmap.Render(element);
                dc.PushOpacity(Math.Clamp(opacity, 0, 1));
                dc.DrawImage(bitmap, new Rect(rect.Left - left, rect.Top - top, rect.Width, rect.Height));
                dc.Pop();
            }
        }

        var target = new RenderTargetBitmap((int)Math.Ceiling(right - left), (int)Math.Ceiling(bottom - top), 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        Save(target, name);
    }

    /// <summary>
    /// A backdrop image: a fully qualified local path (no network share) to a PNG, JPEG or BMP of at most 64 MB, decoded
    /// no larger than the captured area needs.
    /// </summary>
    private static BitmapSource LoadBackdrop(string path, Rect area)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
            || extension is not (".png" or ".jpg" or ".jpeg" or ".bmp"))
        {
            throw new ArgumentException("The backdrop must be a fully qualified local .png, .jpg or .bmp path.");
        }

        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaxBackdropBytes)
        {
            throw new IOException("The backdrop image is missing or too large.");
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.DecodePixelWidth = (int)Math.Clamp(Math.Ceiling(Math.Max(area.Width, area.Height)), 1, 8192);
        image.UriSource = new Uri(info.FullName, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>Draws an image scaled to cover <paramref name="area"/>, centered and cropped.</summary>
    private static void DrawCover(DrawingContext dc, BitmapSource image, Rect area)
    {
        var scale = Math.Max(area.Width / Math.Max(1, image.PixelWidth), area.Height / Math.Max(1, image.PixelHeight));
        var width = image.PixelWidth * scale;
        var height = image.PixelHeight * scale;
        dc.PushClip(new RectangleGeometry(area));
        dc.DrawImage(image, new Rect(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        dc.Pop();
    }

    private void CaptureMenu(string kind, string name)
    {
        if (_menu(kind) is not { } menu)
        {
            return;
        }

        var delay = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            menu.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(menu);
            var width = (int)Math.Ceiling(menu.ActualWidth * dpi.DpiScaleX);
            var height = (int)Math.Ceiling(menu.ActualHeight * dpi.DpiScaleY);
            if (width > 0 && height > 0)
            {
                var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bitmap.Render(menu);
                Save(bitmap, name);
            }

            menu.IsOpen = false;
        }, menu.Dispatcher);
        delay.Start();
    }

    private static BitmapSource OnBackdrop(BitmapSource image, string backdrop)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var area = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
            dc.DrawRectangle(new SolidColorBrush((Color)ColorConverter.ConvertFromString(backdrop)), null, area);
            dc.DrawImage(image, area);
        }

        var target = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        return target;
    }

    private void CaptureElement(string name, string backdrop, string target = "settings")
    {
        if (_element(target) is not { } element)
        {
            return;
        }

        var delay = new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Background, (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            element.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(element);
            var width = (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX);
            var height = (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var content = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            content.Render(element);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush((Color)ColorConverter.ConvertFromString(backdrop)), null, new Rect(0, 0, width, height));
                dc.DrawImage(content, new Rect(0, 0, width, height));
            }

            var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            Save(target, name);
        }, element.Dispatcher);
        delay.Start();
    }

    private void Save(BitmapSource bitmap, string name)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(_folder, name + ".png"));
        encoder.Save(stream);
        AppLog.Info($"Captured {name}.png");
    }
}
