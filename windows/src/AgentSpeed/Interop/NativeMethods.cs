using System.Runtime.InteropServices;

namespace AgentSpeed.Interop;

internal static unsafe partial class User32
{
    private const string Dll = "user32.dll";

    [LibraryImport(Dll, EntryPoint = "RegisterClassExW", SetLastError = true)]
    internal static partial ushort RegisterClassEx(in WNDCLASSEX lpwcx);

    [LibraryImport(Dll, EntryPoint = "UnregisterClassW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterClass(string lpClassName, nint hInstance);

    [LibraryImport(Dll, EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindowEx(
        int dwExStyle, string lpClassName, string? lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint hWnd);

    [LibraryImport(Dll, EntryPoint = "DefWindowProcW")]
    internal static partial nint DefWindowProc(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial nint BeginDeferWindowPos(int nNumWindows);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial nint DeferWindowPos(nint hWinPosInfo, nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EndDeferWindowPos(nint hWinPosInfo);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetClientRect(nint hWnd, out RECT lpRect);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial int MapWindowPoints(nint hWndFrom, nint hWndTo, ref RECT lpPoints, uint cPoints);

    [LibraryImport(Dll, EntryPoint = "FindWindowW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint FindWindow(string? lpClassName, string? lpWindowName);

    [LibraryImport(Dll, EntryPoint = "FindWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint FindWindowEx(nint hWndParent, nint hWndChildAfter, string? lpszClass, string? lpszWindow);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint hWnd);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport(Dll, EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial nint GetWindowLongPtr64(nint hWnd, int nIndex);

    [LibraryImport(Dll, EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLong32(nint hWnd, int nIndex);

    [LibraryImport(Dll, EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport(Dll, EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static partial int SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

    internal static nint GetWindowLongPtr(nint hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

    /// <summary>Sets a window long; returns false only when the call really failed (previous value 0 and an error code).</summary>
    internal static bool TrySetWindowLongPtr(nint hWnd, int nIndex, nint value)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, value)
            : SetWindowLong32(hWnd, nIndex, (int)value);
        return previous != 0 || Marshal.GetLastPInvokeError() == 0;
    }

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial nint SetParent(nint hWndChild, nint hWndNewParent);

    [LibraryImport(Dll)]
    internal static partial nint GetParent(nint hWnd);

    [LibraryImport(Dll)]
    internal static partial nint GetAncestor(nint hWnd, uint gaFlags);

    [LibraryImport(Dll)]
    internal static partial nint GetWindow(nint hWnd, uint uCmd);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateLayeredWindow(
        nint hWnd, nint hdcDst, POINT* pptDst, SIZE* psize, nint hdcSrc, POINT* pptSrc, uint crKey,
        BLENDFUNCTION* pblend, uint dwFlags);

    [LibraryImport(Dll)]
    internal static partial nint GetDC(nint hWnd);

    [LibraryImport(Dll)]
    internal static partial int ReleaseDC(nint hWnd, nint hDC);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport(Dll)]
    internal static partial nint SetCapture(nint hWnd);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReleaseCapture();

    [LibraryImport(Dll)]
    internal static partial nint GetCapture();

    [LibraryImport(Dll)]
    internal static partial nint SetCursor(nint hCursor);

    [LibraryImport(Dll)]
    internal static partial uint GetDpiForWindow(nint hWnd);

    [LibraryImport(Dll)]
    internal static partial int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [LibraryImport(Dll)]
    internal static partial nint MonitorFromPoint(POINT pt, uint dwFlags);

    [LibraryImport(Dll)]
    internal static partial nint MonitorFromWindow(nint hWnd, uint dwFlags);

    [LibraryImport(Dll)]
    internal static partial nint MonitorFromRect(in RECT lprc, uint dwFlags);

    [LibraryImport(Dll, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX lpmi);

    [LibraryImport(Dll, EntryPoint = "EnumDisplaySettingsW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDisplayMonitors(
        nint hdc, nint lprcClip, delegate* unmanaged[Stdcall]<nint, nint, RECT*, nint, int> lpfnEnum, nint dwData);

    [LibraryImport(Dll, EntryPoint = "RegisterWindowMessageW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterWindowMessage(string lpString);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport(Dll)]
    internal static partial nint GetForegroundWindow();

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllowSetForegroundWindow(int dwProcessId);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumChildWindows(nint hWndParent, delegate* unmanaged[Stdcall]<nint, nint, int> lpEnumFunc, nint lParam);

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, nint pChangeFilterStruct);

    [LibraryImport(Dll, EntryPoint = "GetClassNameW", SetLastError = true)]
    internal static partial int GetClassName(nint hWnd, char* lpClassName, int nMaxCount);

    [LibraryImport(Dll, EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(nint hWnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport(Dll)]
    internal static partial nint GetShellWindow();

    [LibraryImport(Dll)]
    internal static partial nint GetDesktopWindow();

    [LibraryImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint hIcon);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial nint CreateIconIndirect(in ICONINFO piconinfo);

    [LibraryImport(Dll, EntryPoint = "PrivateExtractIconsW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint PrivateExtractIcons(
        string szFileName, int nIconIndex, int cxIcon, int cyIcon, nint* phicon, uint* piconid, uint nIcons, uint flags);

    [LibraryImport(Dll, EntryPoint = "LoadCursorW")]
    internal static partial nint LoadCursor(nint hInstance, nint lpCursorName);

    [LibraryImport(Dll, EntryPoint = "SetWindowTextW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowText(nint hWnd, string lpString);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport(Dll)]
    internal static partial nint SetWinEventHook(
        uint eventMin, uint eventMax, nint hmodWinEventProc,
        delegate* unmanaged[Stdcall]<nint, uint, nint, int, int, uint, uint, void> pfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWinEvent(nint hWinEventHook);

    internal static string GetClassName(nint hWnd)
    {
        var buffer = stackalloc char[256];
        var length = GetClassName(hWnd, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }
}

internal static partial class Shell32
{
    private const string Dll = "shell32.dll";

    [LibraryImport(Dll, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [LibraryImport(Dll)]
    internal static partial int Shell_NotifyIconGetRect(in NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

    [LibraryImport(Dll)]
    internal static partial nuint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [LibraryImport(Dll)]
    internal static partial int SHQueryUserNotificationState(out int pquns);
}

internal static partial class Gdi32
{
    private const string Dll = "gdi32.dll";

    internal const uint DIB_RGB_COLORS = 0;
    internal const uint BI_RGB = 0;

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteDC(nint hdc);

    [LibraryImport(Dll)]
    internal static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint ho);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial nint CreateDIBSection(nint hdc, in BITMAPINFO pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

    [LibraryImport(Dll, SetLastError = true)]
    internal static partial nint CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, nint lpBits);
}

internal static unsafe partial class Dwmapi
{
    private const string Dll = "dwmapi.dll";

    [LibraryImport(Dll)]
    internal static partial int DwmSetWindowAttribute(nint hwnd, int dwAttribute, void* pvAttribute, int cbAttribute);

    [LibraryImport(Dll)]
    internal static partial int DwmGetWindowAttribute(nint hwnd, int dwAttribute, void* pvAttribute, int cbAttribute);

    [LibraryImport(Dll)]
    internal static partial int DwmExtendFrameIntoClientArea(nint hWnd, in MARGINS pMarInset);
}

internal static partial class Shcore
{
    internal const int MDT_EFFECTIVE_DPI = 0;

    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}

internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandle(string? lpModuleName);
}
