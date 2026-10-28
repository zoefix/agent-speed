using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace AgentSpeed.Core.IO;

/// <summary>Stable file identity: birth time, native volume/device and file index/inode.</summary>
internal readonly record struct FileIdentity(long CreationTicks, uint Volume, ulong FileIndex, ulong Device = 0)
{
    /// <summary>Null where no stable identity is available (creation time is not reliable on every platform).</summary>
    public static FileIdentity? From(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            return NativeMethods.GetFileInformationByHandle(handle, out var info)
                ? new FileIdentity(
                    ((long)info.CreationTimeHigh << 32) | info.CreationTimeLow,
                    info.VolumeSerialNumber,
                    ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow)
                : null;
        }

        if (OperatingSystem.IsLinux())
        {
            // statx has a fixed ABI across Linux architectures; fstat's struct stat does not.
            if (LinuxFileIdentity.Statx(handle, "", 0x1000 | 0x4000, 0x900, out var stat) == 0 && (stat.Mask & 0x100) != 0)
            {
                var device = ((ulong)stat.DeviceMajor << 32) | stat.DeviceMinor;
                var birth = (stat.Mask & 0x800) != 0 ? stat.BirthSeconds * 10_000_000 + stat.BirthNanoseconds / 100 : 0;
                return new FileIdentity(birth, 0, stat.Inode, device);
            }
            return null;
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            return new FileIdentity(File.GetCreationTimeUtc(handle).Ticks, 0, 0);
        }

        return null;
    }
}

internal static partial class LinuxFileIdentity
{
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    internal struct StatxInfo
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(80)] public long BirthSeconds;
        [FieldOffset(88)] public uint BirthNanoseconds;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [SupportedOSPlatform("linux")]
    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Statx(SafeFileHandle fd, string path, int flags, uint mask, out StatxInfo stat);
}

internal static partial class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandle(
        SafeFileHandle file, out ByHandleFileInformation information);
}
