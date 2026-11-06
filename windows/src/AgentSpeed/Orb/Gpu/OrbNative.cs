using System.Runtime.InteropServices;

namespace AgentSpeed.Orb.Gpu;

/// <summary>Win32 calls the composition orb window needs beyond the shared interop.</summary>
internal static partial class OrbNative
{
    public const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    public const int CS_DBLCLKS = 0x0008;
    public const uint LWA_ALPHA = 0x00000002;
    public const uint WM_GETOBJECT = 0x003D;
    public const uint WM_NCCREATE = 0x0081;
    public const uint WM_CAPTURECHANGED = 0x0215;
    public const uint WM_POWERBROADCAST = 0x0218;
    public const uint WM_WTSSESSION_CHANGE = 0x02B1;
    public const uint PBT_POWERSETTINGCHANGE = 0x8013;
    public const int HTCLIENT = 1;
    public const int UiaRootObjectId = -25;
    public const nint IDC_SIZEALL = 32646;
    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;
    public const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    /// <summary>QS_INPUT: mouse, keyboard, raw input, touch and pointer messages.</summary>
    public const uint QS_INPUT = 0x0C07;

    /// <summary>GUID_CONSOLE_DISPLAY_STATE: the console display turned off (0), on (1) or dimmed (2).</summary>
    public static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    [LibraryImport("user32.dll")]
    public static partial uint GetDoubleClickTime();

    [LibraryImport("user32.dll")]
    public static partial nint GetMessageExtraInfo();

    [LibraryImport("user32.dll")]
    public static partial uint GetQueueStatus(uint flags);

    [LibraryImport("user32.dll")]
    public static partial nint SetCapture(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseCapture();

    [LibraryImport("user32.dll")]
    public static partial nint GetCapture();

    [LibraryImport("user32.dll")]
    public static partial nint SetCursor(nint hCursor);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint RegisterPowerSettingNotification(nint hRecipient, in Guid powerSettingGuid, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterPowerSettingNotification(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    public struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public uint DataLength;
        public uint Data;
    }

    /// <summary>Input waits in the calling thread's queue (posted messages are retrieved before it).</summary>
    public static bool InputPending() => (GetQueueStatus(QS_INPUT) >> 16 & QS_INPUT) != 0;

    public static int LowWord(nint value) => unchecked((short)(long)value);

    public static int HighWord(nint value) => unchecked((short)((long)value >> 16));
}
