using System.Security.Cryptography;
using System.Text;

namespace AgentSpeed.Core.Lan;

/// <summary>
/// The server's access key (LAN 2.1, 4.4): 12 characters of the Crockford Base32 alphabet from the system's cryptographic
/// generator, shown in groups of four and compared in constant time after hyphens and spaces are removed.
/// </summary>
public static class LanKey
{
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int Length = 12;

    public static string New()
    {
        Span<char> key = stackalloc char[Length];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(key);
    }

    public static bool IsValid(string? key) => key is { Length: Length } && key.All(c => Alphabet.Contains(c, StringComparison.Ordinal));

    /// <summary><c>K7Q4-M2X9-PT3D</c>.</summary>
    public static string Display(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var groups = new List<string>();
        for (var i = 0; i < key.Length; i += 4)
        {
            groups.Add(key.Substring(i, Math.Min(4, key.Length - i)));
        }

        return string.Join('-', groups);
    }

    /// <summary>Hyphens and spaces removed, ASCII letters upper-cased; everything else unchanged.</summary>
    public static string Normalize(string given)
    {
        ArgumentNullException.ThrowIfNull(given);
        var builder = new StringBuilder(given.Length);
        foreach (var c in given)
        {
            if (c is '-' or ' ')
            {
                continue;
            }

            builder.Append(c is >= 'a' and <= 'z' ? (char)(c - 32) : c);
        }

        return builder.ToString();
    }

    /// <summary>A given key against the stored one, in constant time; a different length is a mismatch.</summary>
    public static bool Matches(string? given, string? stored)
    {
        if (given is null || stored is null)
        {
            return false;
        }

        var candidate = Encoding.UTF8.GetBytes(Normalize(given));
        var expected = Encoding.UTF8.GetBytes(stored);
        return candidate.Length == expected.Length && CryptographicOperations.FixedTimeEquals(candidate, expected);
    }
}
