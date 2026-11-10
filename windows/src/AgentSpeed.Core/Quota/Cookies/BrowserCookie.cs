namespace AgentSpeed.Core.Quota.Cookies;

/// <summary>
/// One cookie row of a browser store with the facts the choice needs (QUOTAS 9.10). <see cref="Value"/> is plain text; a
/// Chromium row may carry <see cref="Encrypted"/> instead. Never printed with its value.
/// </summary>
internal sealed class BrowserCookie
{
    public required string Host { get; init; }

    public required string Name { get; init; }

    public string Value { get; init; } = string.Empty;

    public byte[] Encrypted { get; init; } = [];

    /// <summary>Unix seconds; 0 for a session cookie.</summary>
    public long ExpiresS { get; init; }

    /// <summary>Unix milliseconds; 0 when the store keeps none.</summary>
    public long LastAccessMs { get; init; }

    public bool NeedsDecryption => Value.Length == 0 && Encrypted.Length > 0;

    /// <summary>The same row with its decrypted value.</summary>
    public BrowserCookie WithValue(string value) =>
        new() { Host = Host, Name = Name, Value = value, ExpiresS = ExpiresS, LastAccessMs = LastAccessMs };

    public override string ToString() => $"BrowserCookie({Host}, {Name})";
}
