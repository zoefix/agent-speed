using System.Buffers;
using System.Security.Cryptography;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Locations;

namespace AgentSpeed.Core.Quota.Cookies;

internal enum CopyOutcome
{
    Read,

    /// <summary>The running browser holds the store (or its journal) without sharing it.</summary>
    Locked,
    Failed,
}

/// <summary>
/// A browser's cookie database copied with its <c>-wal</c> (and <c>-journal</c>) into a private temporary folder, read
/// there and removed right after (QUOTAS 9.10): SQLite never opens the browser's own files.
/// </summary>
internal static class CookieStoreCopy
{
    public const string FolderPrefix = "AgentSpeed-cookies-";

    /// <summary>All files of one store together; a larger store is skipped rather than copied.</summary>
    public const long MaxStoreBytes = 256L << 20;

    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);
    private const int BufferBytes = 64 << 10;

    private static readonly string[] SideSuffixes = ["-wal", "-journal"];
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    /// <summary>Runs <paramref name="body"/> with the copy's path; <paramref name="result"/> is set only on <see cref="CopyOutcome.Read"/>.</summary>
    public static CopyOutcome Read<T>(string store, string tempRoot, Func<string, T> body, out T? result)
        where T : class
    {
        result = null;
        if (!LocationPaths.IsRegularFile(store) || !Path.IsPathFullyQualified(tempRoot))
        {
            return CopyOutcome.Failed;
        }

        var folder = Path.Combine(tempRoot, FolderPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, "store.sqlite");
            var budget = MaxStoreBytes;
            if (!Copy(store, copy, ref budget))
            {
                return CopyOutcome.Failed;
            }

            foreach (var suffix in SideSuffixes)
            {
                if (LocationPaths.IsRegularFile(store + suffix) && !Copy(store + suffix, copy + suffix, ref budget))
                {
                    return CopyOutcome.Failed;
                }
            }

            result = body(copy);
            return CopyOutcome.Read;
        }
        catch (IOException e) when (e.HResult is SharingViolation or LockViolation)
        {
            return CopyOutcome.Locked;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
        {
            return CopyOutcome.Failed;
        }
        finally
        {
            Remove(folder);
        }
    }

    /// <summary>The <c>WHERE</c> arguments of a site's rows: the site, <c>.site</c>, and any subdomain (<c>LIKE … ESCAPE '\'</c>).</summary>
    public static (string Exact, string Dotted, string Pattern) HostArguments(string site)
    {
        var escaped = site.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        return (site, "." + site, "%." + escaped);
    }

    /// <summary>Removes copies an earlier run could not (it was stopped while reading).</summary>
    public static void SweepStale(string tempRoot, DateTime nowUtc)
    {
        if (!Path.IsPathFullyQualified(tempRoot))
        {
            return;
        }

        try
        {
            foreach (var folder in new DirectoryInfo(tempRoot).EnumerateDirectories(FolderPrefix + "*"))
            {
                if (folder.Name.StartsWith(FolderPrefix, StringComparison.Ordinal)
                    && (folder.Attributes & FileAttributes.ReparsePoint) == 0
                    && nowUtc - folder.CreationTimeUtc > StaleAfter)
                {
                    Remove(folder.FullName);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }

    /// <summary>False when the budget runs out; the source is shared for reading and writing, as the browser expects.</summary>
    private static bool Copy(string source, string target, ref long budget)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 0);
        if (input.Length > budget)
        {
            return false;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 0);
            long total = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > budget)
                {
                    return false;
                }

                output.Write(buffer, 0, read);
            }

            budget -= total;
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void Remove(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
