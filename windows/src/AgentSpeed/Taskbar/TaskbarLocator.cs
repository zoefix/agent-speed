using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AgentSpeed.Interop;
using AgentSpeed.Presentation.Layout;

namespace AgentSpeed.Taskbar;

/// <param name="Occupied">Windows of other programs shown on the taskbar (for example TrafficMonitor), which the widget avoids.</param>
internal readonly record struct TaskbarGeometry(nint Taskbar, nint Tray, RECT TaskbarRect, RECT TrayRect, uint Dpi, bool Vertical,
    ImmutableArray<RECT> Occupied = default)
{
    public double Scale => Dpi / 96.0;
}

/// <summary>Finds the primary taskbar and its notification area. Physical pixels throughout.</summary>
internal static class TaskbarLocator
{
    public const string TaskbarClass = "Shell_TrayWnd";
    public const string TrayClass = "TrayNotifyWnd";

    public static TaskbarGeometry? Find()
    {
        var taskbar = User32.FindWindow(TaskbarClass, null);
        if (taskbar == 0 || !User32.IsWindow(taskbar) || !User32.GetWindowRect(taskbar, out var bar) || bar.IsEmpty)
        {
            return null;
        }

        var tray = default(RECT);
        var trayWindow = User32.FindWindowEx(taskbar, 0, TrayClass, null);
        if (trayWindow != 0 && User32.GetWindowRect(trayWindow, out var trayRect) && !trayRect.IsEmpty
            && trayRect.Left >= bar.Left - 1 && trayRect.Right <= bar.Right + 1)
        {
            tray = trayRect;
        }

        var dpi = User32.GetDpiForWindow(taskbar);
        if (dpi == 0)
        {
            dpi = 96;
        }

        return new TaskbarGeometry(taskbar, trayWindow, bar, tray, dpi, bar.Height > bar.Width, Occupants(taskbar, bar));
    }

    /// <summary>
    /// The rectangles of visible windows that other programs host anywhere inside the taskbar (DESIGN 3, Position on the
    /// taskbar): not the shell's own parts and not this app's (or another Agent Speed's) widget.
    /// </summary>
    private static unsafe ImmutableArray<RECT> Occupants(nint taskbar, RECT bar)
    {
        User32.GetWindowThreadProcessId(taskbar, out var shell);
        var search = new OccupantSearch(shell, (uint)Environment.ProcessId, bar);
        var handle = GCHandle.Alloc(search);
        try
        {
            User32.EnumChildWindows(taskbar, &OccupantProc, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return search.Found.ToImmutable();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OccupantProc(nint hwnd, nint data)
    {
        try
        {
            if (GCHandle.FromIntPtr(data).Target is OccupantSearch search)
            {
                search.Visit(hwnd);
            }
        }
        catch
        {
            // Never let an exception cross the native boundary.
        }

        return 1;
    }

    private sealed class OccupantSearch(uint shell, uint self, RECT bar)
    {
        public ImmutableArray<RECT>.Builder Found { get; } = ImmutableArray.CreateBuilder<RECT>();

        public void Visit(nint hwnd)
        {
            User32.GetWindowThreadProcessId(hwnd, out var process);
            if (process == 0 || process == shell || process == self || !User32.IsWindowVisible(hwnd)
                || !User32.GetWindowRect(hwnd, out var rect) || rect.IsEmpty
                || rect.Right <= bar.Left || rect.Left >= bar.Right || rect.Bottom <= bar.Top || rect.Top >= bar.Bottom
                || User32.GetClassName(hwnd) == TaskbarHost.WidgetClassName)
            {
                return;
            }

            Found.Add(rect);
        }
    }

    /// <summary>Widget rectangle in screen coordinates, <paramref name="offset"/> physical pixels from the default spot.</summary>
    public static RECT? WidgetRect(TaskbarGeometry geometry, int length, double offset)
    {
        var (bar, tray, gap) = Layout(geometry);
        var rect = PlacementMath.TaskbarWidget(bar, tray, length, gap, geometry.Vertical, offset, Occupied(geometry));
        if (rect is not { } r)
        {
            return null;
        }

        return new RECT((int)r.X, (int)r.Y, (int)r.Right, (int)r.Bottom);
    }

    /// <summary>The offset (physical pixels) the widget takes on this taskbar for a requested one; null without room.</summary>
    public static double? WidgetOffset(TaskbarGeometry geometry, int length, double offset)
    {
        var (bar, tray, gap) = Layout(geometry);
        return PlacementMath.TaskbarWidgetOffset(bar, tray, length, gap, geometry.Vertical, offset, Occupied(geometry));
    }

    /// <summary>True when the taskbar is slid off-screen (auto-hide) on its monitor.</summary>
    public static bool IsAutoHidden(TaskbarGeometry geometry)
    {
        var monitor = Monitors.FromRect(geometry.TaskbarRect);
        if (monitor is null)
        {
            return false;
        }

        var m = monitor.Bounds;
        var b = geometry.TaskbarRect;
        var visibleWidth = Math.Min(b.Right, m.Right) - Math.Max(b.Left, m.Left);
        var visibleHeight = Math.Min(b.Bottom, m.Bottom) - Math.Max(b.Top, m.Top);
        var thickness = geometry.Vertical ? visibleWidth : visibleHeight;
        return thickness < 8 * geometry.Scale;
    }

    /// <summary>A full-screen app (game, video, presentation) covers the taskbar's monitor.</summary>
    public static bool IsFullscreenActive(TaskbarGeometry geometry)
    {
        if (Shell32.SHQueryUserNotificationState(out var state) == 0
            && state is QUNS.RUNNING_D3D_FULL_SCREEN or QUNS.PRESENTATION_MODE)
        {
            return true;
        }

        var foreground = User32.GetForegroundWindow();
        if (foreground == 0 || foreground == geometry.Taskbar || foreground == User32.GetShellWindow()
            || foreground == User32.GetDesktopWindow())
        {
            return false;
        }

        var className = User32.GetClassName(foreground);
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            return false;
        }

        if (!User32.GetWindowRect(foreground, out var window))
        {
            return false;
        }

        var monitor = Monitors.FromRect(geometry.TaskbarRect);
        if (monitor is null)
        {
            return false;
        }

        var m = monitor.Bounds;
        return window.Left <= m.Left && window.Top <= m.Top && window.Right >= m.Right && window.Bottom >= m.Bottom;
    }

    private static RectD ToRect(RECT r) => new(r.Left, r.Top, r.Width, r.Height);

    private static RectD[] Occupied(TaskbarGeometry geometry) =>
        geometry.Occupied.IsDefaultOrEmpty ? [] : [.. geometry.Occupied.Select(ToRect)];

    private static (RectD Bar, RectD Tray, double Gap) Layout(TaskbarGeometry geometry) => (
        ToRect(geometry.TaskbarRect),
        geometry.TrayRect.IsEmpty ? EstimatedTray(geometry) : ToRect(geometry.TrayRect),
        Math.Round(TaskbarPosition.Gap * geometry.Scale));

    /// <summary>Without a notification area window, leave room for a typical clock and tray icons.</summary>
    private static RectD EstimatedTray(TaskbarGeometry geometry)
    {
        var b = geometry.TaskbarRect;
        if (geometry.Vertical)
        {
            var h = Math.Round(180 * geometry.Scale);
            return new RectD(b.Left, b.Bottom - h, b.Width, h);
        }

        var w = Math.Round(220 * geometry.Scale);
        return new RectD(b.Right - w, b.Top, w, b.Height);
    }
}
