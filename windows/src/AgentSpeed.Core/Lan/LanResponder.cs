using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Core.Lan;

/// <summary>The server's answer to a request head (LAN 4.3): status, error code (null for a frame or an upgrade) and extra headers.</summary>
/// <param name="KeyAccepted">The request carried the valid key (the device count, DESIGN 14).</param>
public sealed record LanResponse(int Status, string? Error, IReadOnlyList<(string Name, string Value)> Headers, bool KeyAccepted = false)
{
    public bool IsUpgrade => Status == 101;
}

/// <summary>
/// HTTP on the server's port (LAN 4.3, 4.4): the checks in their order — malformed head, lockout, method, path, key, then
/// <c>/v1/live</c> or the WebSocket upgrade — and the response bytes. The lockout is updated as the checks pass.
/// </summary>
public static class LanResponder
{
    public const string LivePath = "/v1/live";
    public const string StreamPath = "/v1/stream";
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private static readonly Dictionary<int, string> Reasons = new()
    {
        [101] = "Switching Protocols",
        [200] = "OK",
        [400] = "Bad Request",
        [401] = "Unauthorized",
        [404] = "Not Found",
        [405] = "Method Not Allowed",
        [426] = "Upgrade Required",
        [429] = "Too Many Requests",
        [503] = "Service Unavailable",
    };

    public static LanResponse Respond(LanRequestHead head, string peer, long nowMs, string key, bool hasFrame, LanLockout lockout)
    {
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(lockout);
        if (head.Malformed)
        {
            return Error(400, "badRequest");
        }

        if (lockout.Locked(peer, nowMs) is { } left)
        {
            return Error(429, "tooManyAttempts", ("Retry-After", left.ToString(CultureInfo.InvariantCulture)));
        }

        if (head.Method != "GET")
        {
            return Error(405, "methodNotAllowed", ("Allow", "GET"));
        }

        var path = head.Path;
        if (path is not (LivePath or StreamPath))
        {
            return Error(404, "notFound");
        }

        if (!LanKey.Matches(RequestKey(head), key))
        {
            lockout.Fail(peer, nowMs);
            return Error(401, "unauthorized", ("WWW-Authenticate", "Bearer realm=\"agent-speed\""));
        }

        lockout.Succeed(peer);
        if (path == LivePath)
        {
            return (hasFrame ? new LanResponse(200, null, []) : Error(503, "starting")) with { KeyAccepted = true };
        }

        if (!Tokens(head.Header("upgrade")).Contains("websocket") || !Tokens(head.Header("connection")).Contains("upgrade"))
        {
            return Error(426, "upgradeRequired", ("Upgrade", "websocket")) with { KeyAccepted = true };
        }

        if (head.Header("sec-websocket-version") != "13" || head.Header("sec-websocket-key") is not { } nonce || !IsWebSocketKey(nonce))
        {
            return Error(400, "badRequest", ("Sec-WebSocket-Version", "13")) with { KeyAccepted = true };
        }

        return new LanResponse(101, null, [("Upgrade", "websocket"), ("Connection", "Upgrade"), ("Sec-WebSocket-Accept", Accept(nonce))], KeyAccepted: true);
    }

    /// <summary>LAN 4.4: the Bearer token, else the first <c>key</c> query parameter (percent-decoded), else none.</summary>
    public static string? RequestKey(LanRequestHead head)
    {
        ArgumentNullException.ThrowIfNull(head);
        if (head.Header("authorization") is { Length: > 0 } authorization)
        {
            var space = authorization.IndexOf(' ', StringComparison.Ordinal);
            var scheme = space < 0 ? authorization : authorization[..space];
            var token = space < 0 ? string.Empty : LanText.Trim(authorization[(space + 1)..]);
            if (scheme.Equals("bearer", StringComparison.OrdinalIgnoreCase) && token.Length > 0)
            {
                return token;
            }
        }

        var question = head.Target.IndexOf('?', StringComparison.Ordinal);
        if (question < 0 || question == head.Target.Length - 1)
        {
            return null;
        }

        foreach (var part in head.Target[(question + 1)..].Split('&'))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals >= 0 && part[..equals] == "key")
            {
                return Uri.UnescapeDataString(part[(equals + 1)..]);
            }
        }

        return null;
    }

    /// <summary>RFC 6455: Base64 of the SHA-1 of the key followed by the protocol GUID.</summary>
    public static string Accept(string key) => Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));

    /// <summary>The response's bytes: a 101 with its headers only, every other status with the JSON headers and the body.</summary>
    public static byte[] Bytes(LanResponse response, byte[]? frame)
    {
        ArgumentNullException.ThrowIfNull(response);
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(response.Status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Reasons[response.Status]).Append("\r\n");
        if (response.IsUpgrade)
        {
            foreach (var (name, value) in response.Headers)
            {
                head.Append(name).Append(": ").Append(value).Append("\r\n");
            }

            return Encoding.ASCII.GetBytes(head.Append("\r\n").ToString());
        }

        var body = response.Error is { } error ? ErrorBody(error) : frame ?? [];
        head.Append("Content-Type: application/json; charset=utf-8\r\n")
            .Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n");
        foreach (var (name, value) in response.Headers)
        {
            head.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        return [.. Encoding.ASCII.GetBytes(head.Append("\r\n").ToString()), .. body];
    }

    /// <summary>LAN 3.6: <c>{"schema":1,"type":"error","error":"&lt;code&gt;"}</c>.</summary>
    public static byte[] ErrorBody(string code) => ReportJson.Object(
        ("schema", ReportJson.Of(LanFrame.Schema)), ("type", ReportJson.Of("error")), ("error", ReportJson.Of(code))).ToCompactBytes();

    private static LanResponse Error(int status, string code, params (string, string)[] headers) => new(status, code, headers);

    private static HashSet<string> Tokens(string? value) =>
        (value ?? string.Empty).Split(',').Select(LanText.Trim).Where(t => t.Length > 0).Select(t => t.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    /// <summary>The Base64 form of 16 bytes: the strict alphabet and padding, no white space.</summary>
    private static bool IsWebSocketKey(string value)
    {
        if (value.Length % 4 != 0 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=')))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[24];
        return Convert.TryFromBase64String(value, bytes, out var written) && written == 16;
    }
}
