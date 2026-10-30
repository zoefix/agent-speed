using AgentSpeed.Interop;
using AgentSpeed.Presentation.Layout;

namespace AgentSpeed.Taskbar;

internal readonly record struct TaskbarGeometry(nint Taskbar, nint Tray, RECT TaskbarRect, RECT TrayRect, uint Dpi, bool Vertical,
    RECT TrafficMonitorRect = default)
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

        return new TaskbarGeometry(taskbar, trayWindow, bar, tray, dpi, bar.Height > bar.Width,
            FindTrafficMonitor(taskbar));
    }

    private static RECT FindTrafficMonitor(nint parent)
    {
        var monitor = User32.FindWindowEx(parent, 0, "#32770", "TrafficMonitorTaskbarWindow");
        if (monitor != 0 && User32.IsWindowVisible(monitor) && User32.GetWindowRect(monitor, out var rect) && !rect.IsEmpty)
        {
            return rect;
        }

        // Windows 10 上的监控窗口可能嵌套在任务栏子窗口中。
        for (var child = User32.FindWindowEx(parent, 0, null, null); child != 0;
            child = User32.FindWindowEx(parent, child, null, null))
        {
            var nested = FindTrafficMonitor(child);
            if (!nested.IsEmpty)
            {
                return nested;
            }
        }

        return default;
    }

    /// <summary>Widget rectangle in screen coordinates, <paramref name="offset"/> physical pixels from the default spot.</summary>
    public static RECT? WidgetRect(TaskbarGeometry geometry, int length, double offset)
    {
        var (bar, tray, gap) = Layout(geometry);
        var rect = PlacementMath.TaskbarWidget(bar, tray, length, gap, geometry.Vertical, offset, ToRect(geometry.TrafficMonitorRect));
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
        return PlacementMath.TaskbarWidgetOffset(bar, tray, length, gap, geometry.Vertical, offset, ToRect(geometry.TrafficMonitorRect));
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
