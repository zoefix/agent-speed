using System.Security.Cryptography;
using System.Text;

namespace AgentSpeed.Core.Lan;

/// <summary>A target key's signature (LAN 5.4): <c>sha256=</c> and the lowercase hex HMAC-SHA256 of the frame's exact bytes.</summary>
public static class LanSignature
{
    public const string HeaderName = "X-AgentSpeed-Signature";

    public static string Of(string key, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(key);
        return "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), body));
    }
}

/// <summary>
/// A UDP datagram (LAN 3.5, 5.2): the frame's bytes, with a target key followed by <c>\n</c> and the signature line, at most
/// 1400 bytes; <c>history</c> and then <c>sources</c> are left out while it would be larger.
/// </summary>
public static class LanDatagram
{
    public const int MaxBytes = 1400;

    /// <returns>The datagram and the members left out, in the order they were dropped.</returns>
    public static (byte[] Bytes, IReadOnlyList<string> Omitted) Build(LanFrame frame, string? key)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var omitted = new List<string>();
        byte[] data = [];
        foreach (var drop in new[] { string.Empty, "history", "sources" })
        {
            if (drop.Length > 0)
            {
                omitted.Add(drop);
            }

            var body = frame.Json(history: !omitted.Contains("history"), sources: !omitted.Contains("sources")).ToCompactBytes();
            data = key is null ? body : [.. body, (byte)'\n', .. Encoding.ASCII.GetBytes(LanSignature.Of(key, body))];
            if (data.Length <= MaxBytes)
            {
                break;
            }
        }

        return (data, omitted);
    }
}
