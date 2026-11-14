using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static AgentSpeed.Core.Quota.Local.JobProcessNative;
using static AgentSpeed.Core.Remote.Windows.PipeProcessNative;

namespace AgentSpeed.Core.Quota.Local;

/// <summary>A binary as its version is kept: resolved path, size, modification time (FILETIME) and file id.</summary>
internal readonly record struct AgyIdentity(string Path, long Size, long Modified, uint Volume, ulong Index);

/// <summary>
/// The <c>agy</c> executable of QUOTAS 9.4 <i>Binary</i> on Windows (unverified):
/// <c>%USERPROFILE%\.local\bin\agy.exe</c>, else the first <c>agy.exe</c> on the app's <c>PATH</c>, a regular file only.
/// Relative and network <c>PATH</c> entries are skipped.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AgyBinary
{
    public const string FileName = "agy.exe";

    private const uint FileNameNormalized = 0;

    /// <summary>The binary to run and its identity, or null when there is none.</summary>
    public static (string Executable, AgyIdentity Identity)? Find(string home, string? path)
    {
        foreach (var candidate in Candidates(home, path))
        {
            if (Identity(candidate) is { } identity)
            {
                return (candidate, identity);
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates(string home, string? path)
    {
        yield return Path.Combine(home, ".local", "bin", FileName);
        foreach (var entry in (path ?? string.Empty).Split(';'))
        {
            var folder = entry.Trim().Trim('"');
            if (folder.Length == 0 || !Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal))
            {
                continue;
            }

            yield return Path.Combine(folder, FileName);
        }
    }

    /// <summary>The identity of a regular file on disk (links followed); null for anything else or an unreadable one.</summary>
    public static unsafe AgyIdentity? Identity(string file)
    {
        try
        {
            using var handle = CreateFile(file, FileReadAttributes, FileShareAll, null, OpenExisting, 0, 0);
            if (handle.IsInvalid || GetFileType(handle) != FileTypeDisk || !GetFileInformationByHandle(handle, out var info)
                || (info.FileAttributes & FileAttributeDirectory) != 0)
            {
                return null;
            }

            var buffer = stackalloc char[1024];
            var length = GetFinalPathNameByHandle(handle, buffer, 1024, FileNameNormalized);
            if (length is 0 or >= 1024)
            {
                return null;
            }

            var resolved = new string(buffer, 0, (int)length);
            resolved = resolved.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + resolved[8..]
                : resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ? resolved[4..]
                : resolved;
            return new AgyIdentity(resolved, ((long)info.FileSizeHigh << 32) | info.FileSizeLow,
                ((long)info.LastWriteTimeHigh << 32) | info.LastWriteTimeLow, info.VolumeSerialNumber,
                ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
        }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
            return null;
        }
    }
}
