using AgentSpeed.Interop;
using AgentSpeed.Presentation.Layout;

namespace AgentSpeed.Orb;

/// <summary>A window rectangle in physical pixels.</summary>
internal readonly record struct PixelRect(int Left, int Top, int Width, int Height);

/// <summary>
/// A window that travels with an orb (its name plate, its undo chip): the orb moves it in the same
/// <c>DeferWindowPos</c> batch as itself, so it never lags a frame behind.
/// </summary>
internal interface IOrbAttachment
{
    nint Handle { get; }

    /// <summary>The window's rectangle for the disc drawn at <paramref name="center"/> (physical px); null while hidden.</summary>
    PixelRect? Arrange(PointD center, double scale);

    /// <summary>The orb left the screen (hide when idle) or came back: the window goes and returns with it.</summary>
    void SetAway(bool away);
}

/// <summary>Moves several top-level windows at once (one <c>DeferWindowPos</c> batch, so they move in the same frame).</summary>
internal static class WindowBatch
{
    private const uint Flags = SWP.NOZORDER | SWP.NOACTIVATE | SWP.NOOWNERZORDER;

    public static void Move(IReadOnlyList<(nint Handle, PixelRect Rect)> windows)
    {
        if (windows.Count == 0)
        {
            return;
        }

        if (windows.Count > 1)
        {
            var batch = User32.BeginDeferWindowPos(windows.Count);
            foreach (var (handle, r) in windows)
            {
                if (batch == 0)
                {
                    break;
                }

                batch = User32.DeferWindowPos(batch, handle, 0, r.Left, r.Top, Math.Max(1, r.Width), Math.Max(1, r.Height), Flags);
            }

            if (batch != 0 && User32.EndDeferWindowPos(batch))
            {
                return;
            }
        }

        foreach (var (handle, r) in windows)
        {
            User32.SetWindowPos(handle, 0, r.Left, r.Top, Math.Max(1, r.Width), Math.Max(1, r.Height), Flags);
        }
    }
}
