using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AgentSpeed.Core.Lan;

/// <summary>
/// An HTTP target's request (LAN 5.3) over a plain TCP socket to the checked address: no proxy, no redirect, no TLS. Only the
/// status line is read; no connection, a refusal or no status line within 3 s is unreachable.
/// </summary>
public static class LanHttpPost
{
    public const int TimeoutMs = 3000;
    private const int StatusLineLimit = 1024;

    public static byte[] Request(LanTargetAddress address, byte[] body, string? key, string userAgent)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(body);
        var head = new StringBuilder()
            .Append("POST ").Append(address.Target).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(address.HostHeader).Append("\r\n")
            .Append("User-Agent: ").Append(userAgent).Append("\r\n")
            .Append("Content-Type: application/json\r\n")
            .Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        if (key is not null)
        {
            head.Append(LanSignature.HeaderName).Append(": ").Append(LanSignature.Of(key, body)).Append("\r\n");
        }

        head.Append("Connection: close\r\n\r\n");
        return [.. Encoding.UTF8.GetBytes(head.ToString()), .. body];
    }

    /// <summary>The status code of the reply, or null when the device is unreachable.</summary>
    public static async Task<int?> SendAsync(IPEndPoint endpoint, byte[] request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeoutMs);
        using var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
            var sent = 0;
            while (sent < request.Length)
            {
                sent += await socket.SendAsync(request.AsMemory(sent), SocketFlags.None, timeout.Token).ConfigureAwait(false);
            }

            var buffer = new byte[StatusLineLimit];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await socket.ReceiveAsync(buffer.AsMemory(count), SocketFlags.None, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                count += read;
                if (Array.IndexOf(buffer, (byte)'\n', 0, count) >= 0)
                {
                    break;
                }
            }

            return StatusCode(buffer.AsSpan(0, count));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary><c>HTTP/1.x NNN …</c> up to the first line end; null without a status line.</summary>
    public static int? StatusCode(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf((byte)'\n');
        if (end < 0)
        {
            return null;
        }

        var line = Encoding.ASCII.GetString(data[..end]).TrimEnd('\r');
        var parts = line.Split(' ', 3);
        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/1.", StringComparison.Ordinal) || parts[1].Length != 3
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code))
        {
            return null;
        }

        return code is >= 100 and <= 999 ? code : null;
    }
}
