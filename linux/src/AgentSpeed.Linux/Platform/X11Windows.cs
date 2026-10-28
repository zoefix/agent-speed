using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace AgentSpeed.Linux.Platform;

internal static partial class X11Windows
{
    public static void Activate(Window window)
    {
        var handle = window.TryGetPlatformHandle(); if (handle?.HandleDescriptor != "XID") return;
        var display = XOpenDisplay(IntPtr.Zero); if (display == IntPtr.Zero) return;
        try
        {
            // This path is called only for an explicit launcher/tray/orb action.
            // Use the desktop-tool source so Mutter accepts that user request.
            var message = new XClientEvent { Type = 33, Display = display, Window = handle.Handle,
                MessageType = XInternAtom(display, "_NET_ACTIVE_WINDOW", 0), Format = 32, Source = 2 };
            XSendEvent(display, XDefaultRootWindow(display), 0, (1L << 20) | (1L << 19), ref message);
            XFlush(display);
        }
        finally { XCloseDisplay(display); }
    }
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct XClientEvent
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(24)] public IntPtr Display;
        [FieldOffset(32)] public IntPtr Window;
        [FieldOffset(40)] public IntPtr MessageType;
        [FieldOffset(48)] public int Format;
        [FieldOffset(56)] public IntPtr Source;
    }
    // X Shape input regions do not change rendering or keyboard focus. An empty region passes pointer events through.
    public static unsafe void SetClickThrough(Window window, bool enabled, double? discSize = null, bool namePlate = false)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle?.HandleDescriptor != "XID") return;
        var display = XOpenDisplay(IntPtr.Zero); if (display == IntPtr.Zero) return;
        try
        {
            XShapeCombineMask(display, handle.Handle, 2, 0, 0, IntPtr.Zero, 0);
            if (enabled) XShapeCombineRectangles(display, handle.Handle, 2, 0, 0, IntPtr.Zero, 0, 0, 0);
            else if (discSize is { } size)
            {
                // Transparent glow and an absent name plate must not intercept clicks.
                var array = BuildOrbInputRegion(size, window.RenderScaling, namePlate);
                fixed (XRectangle* data = array) XShapeCombineRectangles(display, handle.Handle, 2, 0, 0, (IntPtr)data, array.Length, 0, 0);
            }
            XFlush(display);
        }
        finally { XCloseDisplay(display); }
    }
    internal static XRectangle[] BuildOrbInputRegion(double size, double scale, bool namePlate)
    {
        var radius = size * scale / 2;
        var center = radius * 1.5;
        var rectangles = new List<XRectangle>();
        for (var y = (int)Math.Ceiling(center - radius); y < center + radius; y++)
        {
            var half = Math.Sqrt(Math.Max(0, radius * radius - Math.Pow(y + .5 - center, 2)));
            var left = (int)Math.Floor(center - half); var right = (int)Math.Ceiling(center + half);
            if (right > left) rectangles.Add(new((short)left, (short)y, (ushort)(right - left), 1));
        }
        if (namePlate) rectangles.Add(new(0, (short)Math.Ceiling(center * 2), (ushort)Math.Ceiling(center * 2), (ushort)Math.Ceiling(30 * scale)));
        return rectangles.ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct XRectangle(short X, short Y, ushort Width, ushort Height);
    internal static int InputRectangleCount(Window window)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle?.HandleDescriptor != "XID") return -1;
        var display = XOpenDisplay(IntPtr.Zero); if (display == IntPtr.Zero) return -1;
        try
        {
            var rectangles = XShapeGetRectangles(display, handle.Handle, 2, out var count, out _);
            if (rectangles != IntPtr.Zero) XFree(rectangles);
            return count;
        }
        finally { XCloseDisplay(display); }
    }
    internal static bool InputContains(Window window, int x, int y)
    {
        var handle = window.TryGetPlatformHandle(); if (handle?.HandleDescriptor != "XID") return false;
        var display = XOpenDisplay(IntPtr.Zero); if (display == IntPtr.Zero) return false;
        try
        {
            var rectangles = XShapeGetRectangles(display, handle.Handle, 2, out var count, out _);
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var r = Marshal.PtrToStructure<XRectangle>(rectangles + i * Marshal.SizeOf<XRectangle>());
                    if (x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height) return true;
                }
                return false;
            }
            finally { if (rectangles != IntPtr.Zero) XFree(rectangles); }
        }
        finally { XCloseDisplay(display); }
    }
    [LibraryImport("libX11.so.6")] private static partial IntPtr XOpenDisplay(IntPtr name);
    [LibraryImport("libX11.so.6")] private static partial int XCloseDisplay(IntPtr display);
    [LibraryImport("libX11.so.6")] private static partial int XFlush(IntPtr display);
    [LibraryImport("libX11.so.6")] private static partial int XFree(IntPtr pointer);
    [LibraryImport("libX11.so.6")] private static partial IntPtr XDefaultRootWindow(IntPtr display);
    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr XInternAtom(IntPtr display, string name, int onlyIfExists);
    [LibraryImport("libX11.so.6")] private static partial int XSendEvent(IntPtr display, IntPtr window, int propagate, long mask, ref XClientEvent message);
    [LibraryImport("libXext.so.6")] private static partial IntPtr XShapeGetRectangles(IntPtr display, IntPtr window, int kind, out int count, out int ordering);
    [LibraryImport("libXext.so.6")] private static partial void XShapeCombineMask(IntPtr display, IntPtr window, int kind, int x, int y, IntPtr pixmap, int operation);
    [LibraryImport("libXext.so.6")] private static partial void XShapeCombineRectangles(IntPtr display, IntPtr window, int kind, int x, int y,
        IntPtr rectangles, int count, int operation, int ordering);
}
