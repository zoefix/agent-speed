using System.Security.Cryptography;

namespace AgentSpeed.Presentation.Updates;

/// <summary><c>SHA256SUMS.txt</c>: one <c>&lt;sha256&gt;  &lt;file name&gt;</c> per line, as written by sha256sum.</summary>
public static class ChecksumManifest
{
    public const int MaxBytes = 64 * 1024;
    private const int HashHexLength = 64;

    /// <summary>
    /// The expected SHA-256 of <paramref name="fileName"/>, or null when it is missing, malformed, or listed twice with
    /// different values.
    /// </summary>
    public static byte[]? Find(string text, string fileName)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        if (text.Length > MaxBytes)
        {
            return null;
        }

        byte[]? found = null;
        foreach (var rawLine in text.Split('\n'))
        {
            if (!TryParseLine(rawLine, out var hash, out var name) || !string.Equals(name, fileName, StringComparison.Ordinal))
            {
                continue;
            }

            if (found is not null && !found.AsSpan().SequenceEqual(hash))
            {
                return null;
            }

            found = hash;
        }

        return found;
    }

    /// <summary>Constant-time comparison of two SHA-256 digests.</summary>
    public static bool Matches(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual) =>
        expected.Length == SHA256.HashSizeInBytes && actual.Length == SHA256.HashSizeInBytes
        && CryptographicOperations.FixedTimeEquals(expected, actual);

    public static async Task<byte[]> ComputeAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    internal static bool TryParseLine(string line, out byte[] hash, out string name)
    {
        hash = [];
        name = string.Empty;
        var span = line.AsSpan().TrimEnd();
        if (span.Length < HashHexLength + 2 || !char.IsWhiteSpace(span[HashHexLength]))
        {
            return false;
        }

        var hex = span[..HashHexLength];
        foreach (var c in hex)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        var rest = span[HashHexLength..].TrimStart();
        if (rest.Length > 0 && rest[0] == '*')
        {
            rest = rest[1..];
        }

        if (rest.StartsWith("./", StringComparison.Ordinal))
        {
            rest = rest[2..];
        }

        if (rest.IsEmpty)
        {
            return false;
        }

        hash = Convert.FromHexString(hex);
        name = rest.ToString();
        return true;
    }
}
