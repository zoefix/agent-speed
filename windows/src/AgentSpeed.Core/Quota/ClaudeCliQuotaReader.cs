namespace AgentSpeed.Core.Quota;

/// <summary>
/// The app's side of the quota file (QUOTAS 5.5 <i>Reader</i>): checked by modification time, read when it changed.
/// Not thread-safe: one caller at a time.
/// </summary>
public sealed class ClaudeCliQuotaReader(string quotaFile)
{
    public const long CheckIntervalMs = 5_000;

    private DateTime? _known;
    private long _knownLength = -1;

    public QuotaSnapshot? Snapshot { get; private set; }

    /// <summary>When the quota file was last written (local time for Settings' status), null without one.</summary>
    public DateTimeOffset? FileTime { get; private set; }

    /// <summary>Reads the file when its time or size changed; true when the snapshot changed.</summary>
    public bool Poll()
    {
        var info = new FileInfo(quotaFile);
        if (!info.Exists)
        {
            var had = Snapshot is not null;
            Snapshot = null;
            FileTime = null;
            _known = null;
            _knownLength = -1;
            return had;
        }

        if (_known == info.LastWriteTimeUtc && _knownLength == info.Length)
        {
            return false;
        }

        _known = info.LastWriteTimeUtc;
        _knownLength = info.Length;
        FileTime = new DateTimeOffset(info.LastWriteTimeUtc);
        byte[] bytes;
        try
        {
            using var stream = new FileStream(quotaFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > ClaudeCliQuota.InputLimit)
            {
                return false;
            }

            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _known = null;
            return false;
        }

        var next = ToSnapshot(ClaudeCliQuota.Read(bytes), FileTime.Value.ToUnixTimeMilliseconds());
        var changed = !Equals(next, Snapshot);
        Snapshot = next;
        return changed;
    }

    /// <summary>Each stored window as <c>fiveHour</c> / <c>sevenDay</c>, observed when it was received (else the file's time).</summary>
    internal static QuotaSnapshot ToSnapshot(CliQuotaState state, long fileMs)
    {
        var windows = new List<QuotaWindow>(2);
        if (state.FiveHour is { } five)
        {
            windows.Add(QuotaWindow.Of(QuotaWindowKind.FiveHour, five.UsedPercentage, five.ResetsAtMs));
        }

        if (state.SevenDay is { } seven)
        {
            windows.Add(QuotaWindow.Of(QuotaWindowKind.SevenDay, seven.UsedPercentage, seven.ResetsAtMs));
        }

        return new QuotaSnapshot(SourceKind.ClaudeCode, state.ReceivedMs ?? fileMs, windows);
    }
}
