using System.Text.RegularExpressions;
using AgentSpeed.Core.Traffic.Engine;
using AgentSpeed.Core.Traffic.Monitor;

namespace AgentSpeed.Core.Quota;

/// <summary>
/// Which quota apps run (QUOTAS 6): a <c>codex</c> or <c>claudeCode</c> agent process (METRICS 10.5), the Codex app's
/// main process, and Claude Desktop's main process. Its own light listing of this session's processes, classified by the
/// traffic rules; paths are read once per process. Not thread-safe.
/// </summary>
public sealed partial class QuotaPresence : IDisposable
{
    /// <summary>The listing runs at most this often, and only while something asks (an orb shows the 額度 card).</summary>
    public const long ListIntervalMs = 15_000;

    private readonly IProcessLister _lister;
    private readonly ProcessTable _table;
    private readonly TrafficPlatform _platform;
    private readonly List<ProcessEntry> _entries = new(512);
    private long? _listedMs;

    [GeneratedRegex("/windowsapps/openai\\.codex_[^/]*/app/codex(\\.exe)?$", RegexOptions.CultureInvariant)]
    private static partial Regex CodexApp();

    internal QuotaPresence(IProcessLister lister, TrafficPlatform platform)
    {
        _lister = lister;
        _platform = platform;
        _table = new ProcessTable(platform, exitedRetentionMs: 0);
    }

    /// <summary>This session's processes; null where no process listing exists (not Windows).</summary>
    public static QuotaPresence? ForThisComputer() =>
        OperatingSystem.IsWindows() ? new QuotaPresence(new Traffic.Windows.ProcessReader(), TrafficPlatform.CurrentWindows()) : null;

    public bool Codex { get; private set; }

    public bool ClaudeCode { get; private set; }

    public bool ClaudeDesktop { get; private set; }

    /// <summary>Claude Desktop's main process appeared in the latest listing (it was not in the one before).</summary>
    public bool ClaudeDesktopStarted { get; private set; }

    public bool Runs(SourceKind source) => source switch
    {
        SourceKind.Codex => Codex,
        SourceKind.ClaudeCode => ClaudeCode,
        SourceKind.ClaudeDesktop => ClaudeDesktop,
        _ => false,
    };

    /// <summary>Lists when <see cref="ListIntervalMs"/> passed (or <paramref name="force"/>); true when a state changed.</summary>
    public bool Refresh(long nowMs, bool force = false)
    {
        if (!force && _listedMs is { } last && nowMs - last < ListIntervalMs)
        {
            ClaudeDesktopStarted = false;
            return false;
        }

        _listedMs = nowMs;
        if (!_lister.List(_entries))
        {
            return false;
        }

        _table.Refresh(_entries, _lister, nowMs);
        bool codex = false, code = false, desktop = false;
        foreach (var record in _table.Records.Values)
        {
            if (!record.Alive)
            {
                continue;
            }

            if (record.Class is { Role: TrafficRole.Agent } agent)
            {
                codex |= agent.Source == SourceKind.Codex;
                code |= agent.Source == SourceKind.ClaudeCode;
            }

            if (record.Executable.Length > 0)
            {
                desktop |= IsClaudeDesktopMain(record.Executable);
                codex |= IsCodexApp(record.Executable);
            }
        }

        var changed = codex != Codex || code != ClaudeCode || desktop != ClaudeDesktop;
        ClaudeDesktopStarted = desktop && !ClaudeDesktop;
        (Codex, ClaudeCode, ClaudeDesktop) = (codex, code, desktop);
        return changed;
    }

    /// <summary>Forgets the listing time, so the next refresh lists at once.</summary>
    public void Invalidate() => _listedMs = null;

    private bool IsClaudeDesktopMain(string executable) => _platform.Name(executable) == "claude" && _table.Rules.IsClaudeDesktopApp(executable);

    private bool IsCodexApp(string executable) => _platform.IsWindows && CodexApp().IsMatch(_platform.Normalize(executable));

    public void Dispose() => (_lister as IDisposable)?.Dispose();
}
