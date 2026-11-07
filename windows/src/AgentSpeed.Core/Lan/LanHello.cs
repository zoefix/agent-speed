using System.Text;
using System.Text.Json;

namespace AgentSpeed.Core.Lan;

/// <summary>The line protocol's first line (LAN 4.6): a JSON object with <c>"type":"hello"</c> and the access key as <c>key</c>.</summary>
public static class LanHello
{
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Null for a valid hello, else the error code to answer with (a missing or wrong key counts as a failure).</summary>
    public static string? Check(ReadOnlySpan<byte> line, string peer, long nowMs, string key, LanLockout lockout)
    {
        ArgumentNullException.ThrowIfNull(lockout);
        if (lockout.Locked(peer, nowMs) is not null)
        {
            return "tooManyAttempts";
        }

        string? given;
        try
        {
            using var document = JsonDocument.Parse(Strict.GetString(line), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "hello"
                || !root.TryGetProperty("key", out var value) || value.ValueKind != JsonValueKind.String)
            {
                return "badRequest";
            }

            given = value.GetString();
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or ArgumentException)
        {
            return "badRequest";
        }

        if (!LanKey.Matches(given, key))
        {
            lockout.Fail(peer, nowMs);
            return "unauthorized";
        }

        lockout.Succeed(peer);
        return null;
    }
}
