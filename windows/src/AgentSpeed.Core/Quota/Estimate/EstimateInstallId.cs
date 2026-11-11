using System.Security.Cryptography;
using AgentSpeed.Core.Network;

namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>
/// The install id of QUOTAS 10.1: 32 random bytes in <c>&lt;data&gt;/quota/estimate-salt</c> (the file keeps the name it had
/// when it held the account salt), created when first needed with a temporary file and a rename, used as 64 hex digits. It
/// keys only the accounts without an id, never leaves the computer and is never logged.
/// </summary>
public static class EstimateInstallId
{
    public const int Bytes = 32;

    /// <summary>The stored id, else a new one written to <paramref name="path"/>; throws when neither works.</summary>
    public static string LoadOrCreate(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Read(path) is { } existing)
        {
            return Convert.ToHexStringLower(existing);
        }

        var bytes = RandomNumberGenerator.GetBytes(Bytes);
        AtomicFile.Write(path, bytes);
        return Convert.ToHexStringLower(Read(path) ?? bytes);
    }

    /// <summary>An id that lives in memory only, for an install whose file cannot be written.</summary>
    public static string Ephemeral() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(Bytes));

    private static byte[]? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != Bytes)
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            return bytes.Length == Bytes ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
