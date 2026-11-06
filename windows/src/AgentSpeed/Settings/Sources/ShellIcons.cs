using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AgentSpeed.Settings.Sources;

/// <summary>The shell's own folder and document icons (as File Explorer draws them), loaded once; null where unavailable.</summary>
internal static partial class ShellIcons
{
    private const uint StockFolder = 3;
    private const uint StockDocument = 0;
    private const uint IconFlag = 0x100;
    private const uint LargeIconFlag = 0x0;

    private static readonly Lazy<ImageSource?> FolderIcon = new(() => Load(StockFolder));
    private static readonly Lazy<ImageSource?> DocumentIcon = new(() => Load(StockDocument));

    public static ImageSource? Folder => FolderIcon.Value;

    public static ImageSource? Document => DocumentIcon.Value;

    private static unsafe ImageSource? Load(uint id)
    {
        var info = new StockIconInfo { Size = (uint)sizeof(StockIconInfo) };
        try
        {
            if (SHGetStockIconInfo(id, IconFlag | LargeIconFlag, ref info) != 0 || info.Icon == 0)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception e) when (e is ExternalException or ArgumentException or EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
        finally
        {
            if (info.Icon != 0)
            {
                DestroyIcon(info.Icon);
            }
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetStockIconInfo(uint id, uint flags, ref StockIconInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct StockIconInfo
    {
        public uint Size;
        public nint Icon;
        public int SystemImageIndex;
        public int IconIndex;
        public fixed char Path[260];
    }
}
