using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AgentSpeed.Core.Traffic.Engine;
using AgentSpeed.Core.Traffic.Monitor;

namespace AgentSpeed.Core.Traffic.Linux;

[SupportedOSPlatform("linux")]
internal sealed partial class HelperSampler : IProcessCounterSource
{
    internal const string SocketPath = "/run/agent-speed-traffic/helper.sock";
    private readonly string _socketPath;
    private readonly List<CounterRow> _rows = [];
    private long _retryAt;
    private string? _epoch;
    public ProcessCounterStatus Status { get; private set; } = ProcessCounterStatus.Idle;
    public int Stream { get; private set; }
    public string? LastError { get; private set; }
    public HelperSampler(string socketPath = SocketPath) => _socketPath = socketPath;

    public IReadOnlyList<CounterRow>? Poll(bool active, bool fetch, long nowMs)
    {
        if (!active) { Status = ProcessCounterStatus.Idle; return null; }
        if (!fetch || nowMs < _retryAt) return null;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.SendTimeout = socket.ReceiveTimeout = 1500;
            socket.Connect(new UnixDomainSocketEndPoint(_socketPath));
            var credentials = new byte[12];
            uint size = 12;
            if (GetSockOpt(socket.Handle.ToInt32(), 1, 17, credentials, ref size) != 0 || BitConverter.ToUInt32(credentials, 4) != 0)
                throw new UnauthorizedAccessException("Helper peer is not root.");
            using var reader = new StreamReader(new NetworkStream(socket, ownsSocket: false));
            var epoch = ReadSample(reader, _rows);
            if (_epoch != epoch) { _epoch = epoch; Stream++; }
            Status = ProcessCounterStatus.Sampling;
            LastError = null;
            return _rows;
        }
        catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Status = ex is UnauthorizedAccessException ? ProcessCounterStatus.HelperDenied
                : !File.Exists(_socketPath) ? ProcessCounterStatus.HelperNotInstalled : ProcessCounterStatus.HelperStopped;
            LastError = ex.Message;
            _retryAt = nowMs + 30_000;
            return null;
        }
    }
    /// <summary>Validate the entire bounded response before accepting its epoch or publishing counters.</summary>
    internal static string ReadSample(TextReader reader, List<CounterRow> rows)
    {
        var header = ReadLine(reader)?.Split(' ');
        if (header is not { Length: 2 } || header[0] != "AGENT_SPEED_TRAFFIC_1")
            throw new InvalidDataException("Unsupported helper protocol.");
        rows.Clear();
        for (var i = 0; i < 131_073; i++)
        {
            var line = ReadLine(reader);
            if (line is null) break;
            if (line == "END") return header[1];
            var fields = line.Split(' ');
            if (fields.Length != 4 || !int.TryParse(fields[0], out var pid) || !long.TryParse(fields[1], out var start)
                || !long.TryParse(fields[2], out var rx) || !long.TryParse(fields[3], out var tx)
                || pid <= 0 || start <= 0 || rx < 0 || tx < 0)
                throw new InvalidDataException("Invalid helper row.");
            rows.Add(new(new(pid, start), new(rx, tx)));
        }
        throw new InvalidDataException("Incomplete helper sample.");
    }

    private static string? ReadLine(TextReader reader)
    {
        var line = new System.Text.StringBuilder();
        for (int c; (c = reader.Read()) >= 0;)
        {
            if (c == '\n') return line.ToString();
            if (line.Length >= 256) throw new InvalidDataException("Helper line too long.");
            line.Append((char)c);
        }
        return null;
    }
    public void Retry() => _retryAt = 0;
    public void Dispose() { Status = ProcessCounterStatus.Idle; }
    [LibraryImport("libc", EntryPoint = "getsockopt")] private static partial int GetSockOpt(int fd, int level, int option,
        [Out] byte[] value, ref uint length);
}
