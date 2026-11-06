using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Automation.Provider;
using AgentSpeed.App;
using AgentSpeed.Interop;

namespace AgentSpeed.Orb.Gpu;

/// <summary>What the composition orb window reports (client coordinates in physical pixels).</summary>
internal interface IOrbHostInput
{
    void OnMouseMove(int x, int y);

    void OnMouseLeave();

    void OnLeftDown(int x, int y, bool doubleClick);

    void OnLeftUp();

    void OnRightUp(int x, int y);

    void OnCaptureLost();

    void OnDpiChanged();

    /// <summary>Whether the client point is on the disc (the cursor shows that the orb can be dragged).</summary>
    bool IsOnDisc(int x, int y);

    /// <summary>Whether the client point is on the unit's hit area (the cursor becomes the pointing hand, DESIGN 9).</summary>
    bool IsOnUnit(int x, int y);

    /// <summary>The UI Automation provider of the window.</summary>
    IRawElementProviderSimple? AutomationProvider { get; }
}

/// <summary>
/// The top-level window of one composition orb: a borderless popup with no redirection bitmap (its pixels come from a
/// DirectComposition visual), layered at full opacity so <c>WS_EX_TRANSPARENT</c> can let clicks through to other
/// processes, never activated, a tool window (no taskbar button, no Alt+Tab).
/// </summary>
internal sealed unsafe class OrbHostWindow : IDisposable
{
    private const string ClassName = "AgentSpeedOrbWindow";
    private static readonly Dictionary<nint, OrbHostWindow> Windows = [];
    private static bool s_registered;
    private static nint s_sizeAll;
    private static nint s_arrow;
    private static nint s_hand;

    private readonly IOrbHostInput _input;
    private bool _trackingLeave;
    private bool _transparent;
    private bool _topmost;

    public OrbHostWindow(string title, IOrbHostInput input, bool topmost)
    {
        _input = input;
        EnsureClass();
        _topmost = topmost;
        var exStyle = OrbNative.WS_EX_NOREDIRECTIONBITMAP | WS_EX.TOOLWINDOW | WS_EX.NOACTIVATE | WS_EX.LAYERED | (topmost ? WS_EX.TOPMOST : 0);
        Handle = User32.CreateWindowEx(exStyle, ClassName, title, WS.POPUP, -32000, -32000, 1, 1, 0, 0, Kernel32.GetModuleHandle(null), 0);
        if (Handle == 0)
        {
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()}).");
        }

        OrbNative.SetLayeredWindowAttributes(Handle, 0, 255, OrbNative.LWA_ALPHA);
        Windows[Handle] = this;
    }

    public nint Handle { get; private set; }

    public static bool IsOrbWindow(nint hwnd) => Windows.ContainsKey(hwnd);

    public bool IsVisible => Handle != 0 && User32.IsWindowVisible(Handle);

    public void Show()
    {
        if (Handle != 0)
        {
            User32.ShowWindow(Handle, OrbNative.SW_SHOWNOACTIVATE);
        }
    }

    public void Hide()
    {
        if (Handle != 0)
        {
            User32.ShowWindow(Handle, OrbNative.SW_HIDE);
        }
    }

    public bool Topmost
    {
        get => _topmost;
        set
        {
            if (_topmost == value || Handle == 0)
            {
                return;
            }

            _topmost = value;
            User32.SetWindowPos(Handle, value ? HWND.TOPMOST : HWND.NOTOPMOST, 0, 0, 0, 0, SWP.NOMOVE | SWP.NOSIZE | SWP.NOACTIVATE);
        }
    }

    /// <summary>Lets every click through (WS_EX_TRANSPARENT on the layered window).</summary>
    public bool Transparent
    {
        get => _transparent;
        set
        {
            if (_transparent == value || Handle == 0)
            {
                return;
            }

            _transparent = value;
            if (value)
            {
                WindowEffects.AddExtendedStyle(Handle, WS_EX.TRANSPARENT, frameChanged: false);
            }
            else
            {
                WindowEffects.AddExtendedStyle(Handle, 0, WS_EX.TRANSPARENT, frameChanged: false);
            }
        }
    }

    public void CaptureMouse() => OrbNative.SetCapture(Handle);

    public void ReleaseMouse()
    {
        if (OrbNative.GetCapture() == Handle)
        {
            OrbNative.ReleaseCapture();
        }
    }

    public void Dispose()
    {
        if (Handle == 0)
        {
            return;
        }

        var handle = Handle;
        Windows.Remove(handle);
        Handle = 0;
        User32.DestroyWindow(handle);
    }

    private nint OnMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case WM.MOUSEACTIVATE:
                return MA.NOACTIVATE;
            case WM.NCHITTEST:
                return OrbNative.HTCLIENT;
            case WM.SETCURSOR:
                if ((lParam & 0xFFFF) == OrbNative.HTCLIENT && User32.GetCursorPos(out var cursor) && User32.GetWindowRect(hwnd, out var rect))
                {
                    var (cx, cy) = (cursor.X - rect.Left, cursor.Y - rect.Top);
                    OrbNative.SetCursor(_input.IsOnUnit(cx, cy) ? s_hand : _input.IsOnDisc(cx, cy) ? s_sizeAll : s_arrow);
                    return 1;
                }

                break;
            case WM.MOUSEMOVE:
                if (!_trackingLeave)
                {
                    var track = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = TME.LEAVE, hwndTrack = hwnd };
                    _trackingLeave = User32.TrackMouseEvent(ref track);
                }

                _input.OnMouseMove(OrbNative.LowWord(lParam), OrbNative.HighWord(lParam));
                return 0;
            case WM.MOUSELEAVE:
                _trackingLeave = false;
                _input.OnMouseLeave();
                return 0;
            case WM.LBUTTONDOWN:
                _input.OnLeftDown(OrbNative.LowWord(lParam), OrbNative.HighWord(lParam), doubleClick: false);
                return 0;
            case WM.LBUTTONDBLCLK:
                _input.OnLeftDown(OrbNative.LowWord(lParam), OrbNative.HighWord(lParam), doubleClick: true);
                return 0;
            case WM.LBUTTONUP:
                _input.OnLeftUp();
                return 0;
            case WM.RBUTTONUP:
                _input.OnRightUp(OrbNative.LowWord(lParam), OrbNative.HighWord(lParam));
                return 0;
            case OrbNative.WM_CAPTURECHANGED:
                _input.OnCaptureLost();
                return 0;
            case WM.DPICHANGED:
                _input.OnDpiChanged();
                return 0;
            case OrbNative.WM_GETOBJECT:
                if ((int)(long)lParam == OrbNative.UiaRootObjectId && _input.AutomationProvider is { } provider)
                {
                    return AutomationInteropProvider.ReturnRawElementProvider(hwnd, (nint)wParam, lParam, provider);
                }

                break;
            case WM.ERASEBKGND:
                return 1;
        }

        return User32.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static void EnsureClass()
    {
        if (s_registered)
        {
            return;
        }

        s_sizeAll = User32.LoadCursor(0, OrbNative.IDC_SIZEALL);
        s_arrow = User32.LoadCursor(0, IDC.ARROW);
        s_hand = User32.LoadCursor(0, IDC.HAND);
        fixed (char* name = ClassName)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)sizeof(WNDCLASSEX),
                style = OrbNative.CS_DBLCLKS,
                lpfnWndProc = &WindowProc,
                hInstance = Kernel32.GetModuleHandle(null),
                hCursor = 0,
                lpszClassName = name,
            };
            if (User32.RegisterClassEx(wc) == 0 && Marshal.GetLastPInvokeError() != Errors.ERROR_CLASS_ALREADY_EXISTS)
            {
                throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastPInvokeError()}).");
            }
        }

        s_registered = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (Windows.TryGetValue(hwnd, out var window))
            {
                return window.OnMessage(hwnd, message, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Orb window message 0x{message:X4} failed.", ex);
        }

        return User32.DefWindowProc(hwnd, message, wParam, lParam);
    }
}
