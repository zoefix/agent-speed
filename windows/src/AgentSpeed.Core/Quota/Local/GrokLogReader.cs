using AgentSpeed.Core.Quota.Network;
using AgentSpeed.Core.Quota.Vendors;

namespace AgentSpeed.Core.Quota.Local;

/// <summary>
/// Grok Build's billing log (QUOTAS 9.2, <i>Log</i>): the last 2 MiB of <c>unified.jsonl</c>, read again only when the file
/// changed. No network. Not thread-safe.
/// </summary>
public sealed class GrokLogReader(string file)
{
    public const int TailBytes = 2 << 20;

    private (long Size, DateTime Modified)? _stamp;

    public string File { get; } = file;

    public QuotaSnapshot? Snapshot { get; private set; }

    /// <summary><c>$GROK_HOME/logs/unified.jsonl</c>, else <c>~/.grok/logs/unified.jsonl</c>.</summary>
    public static string DefaultFile(string home, Func<string, string?> environment) =>
        Path.Combine(QuotaCredentials.Folder("GROK_HOME", environment) ?? Path.Combine(home, ".grok"), "logs", "unified.jsonl");

    /// <summary>True when the snapshot changed.</summary>
    public bool Refresh()
    {
        var info = new FileInfo(File);
        if (!info.Exists)
        {
            _stamp = null;
            return false;
        }

        var stamp = (info.Length, info.LastWriteTimeUtc);
        if (_stamp == stamp)
        {
            return false;
        }

        _stamp = stamp;
        if (TailLines(File) is not { } lines || GrokBilling.FromLog(lines) is not { } found)
        {
            return false;
        }

        var next = new QuotaSnapshot(SourceKind.GrokBuild, found.ObservedMs, found.Windows);
        if (next.Equals(Snapshot))
        {
            return false;
        }

        Snapshot = next;
        return true;
    }

    /// <summary>The file's last 2 MiB as lines; a cut first line is dropped.</summary>
    public static IReadOnlyList<ReadOnlyMemory<byte>>? TailLines(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            var data = new byte[(int)(stream.Length - start)];
            var read = stream.ReadAtLeast(data, data.Length, throwOnEndOfStream: false);
            ReadOnlyMemory<byte> memory = data.AsMemory(0, read);
            var lines = new List<ReadOnlyMemory<byte>>();
            var first = true;
            while (!memory.IsEmpty)
            {
                var end = memory.Span.IndexOf((byte)'\n');
                var line = end < 0 ? memory : memory[..end];
                memory = end < 0 ? ReadOnlyMemory<byte>.Empty : memory[(end + 1)..];
                if (!(first && start > 0) && !line.IsEmpty)
                {
                    lines.Add(line);
                }

                first = false;
            }

            return lines;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
