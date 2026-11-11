using System.Text;
using AgentSpeed.Core.Locations;

namespace AgentSpeed.Core.Quota.Cookies;

/// <summary>Small browser files (<c>Local State</c>, <c>Last Version</c>, <c>profiles.ini</c>) read whole with a size cap.</summary>
internal static class CookieFiles
{
    /// <summary>The file's bytes when it is a regular file of at most <paramref name="maxBytes"/>; null otherwise.</summary>
    public static byte[]? ReadSmall(string path, int maxBytes)
    {
        if (!LocationPaths.IsRegularFile(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maxBytes)
            {
                return null;
            }

            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? ReadText(string path, int maxBytes) =>
        ReadSmall(path, maxBytes) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    /// <summary>A site name as the readers ask for it: lowercase ASCII letters, digits, <c>.</c> and <c>-</c>.</summary>
    public static bool IsSite(string? site) =>
        site is { Length: > 0 and <= 253 } && site[0] != '.' && site[^1] != '.'
        && site.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-');
}
