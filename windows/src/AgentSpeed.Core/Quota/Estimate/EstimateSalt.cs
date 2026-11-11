using System.Security.Cryptography;
using AgentSpeed.Core.Network;

namespace AgentSpeed.Core.Quota.Estimate;

/// <summary>
/// The install salt of QUOTAS 10.1: 32 random bytes in <c>&lt;data&gt;/quota/estimate-salt</c>, created when first needed
/// with a temporary file and a rename. Never logged or reported.
/// </summary>
public static class EstimateSalt
{
    public static byte[] LoadOrCreate(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Read(path) is { } existing)
        {
            return existing;
        }

        var salt = RandomNumberGenerator.GetBytes(AccountKeys.SaltBytes);
        AtomicFile.Write(path, salt);
        return Read(path) ?? salt;
    }

    private static byte[]? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != AccountKeys.SaltBytes)
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            return bytes.Length == AccountKeys.SaltBytes ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
