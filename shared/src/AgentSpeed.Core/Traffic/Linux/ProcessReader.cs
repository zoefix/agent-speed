using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AgentSpeed.Core.Traffic.Engine;
using AgentSpeed.Core.Traffic.Monitor;

namespace AgentSpeed.Core.Traffic.Linux;

[SupportedOSPlatform("linux")]
internal sealed partial class ProcessReader : IProcessLister
{
    private readonly uint _uid = GetUid();
    private readonly long _ticks = Sysconf(2); // _SC_CLK_TCK
    private readonly long _bootUs = ReadBootUs();

    public bool List(List<ProcessEntry> entries)
    {
        entries.Clear();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                if (int.TryParse(Path.GetFileName(directory), out var pid) && Entry(pid) is { } entry) entries.Add(entry);
            }
            return true;
        }
        catch (IOException) { return false; }
    }

    internal ProcessEntry? Entry(int pid)
    {
        try
        {
            // Restrict expensive inspection and classification to the current user's processes.
            var uidLine = File.ReadLines($"/proc/{pid}/status").FirstOrDefault(l => l.StartsWith("Uid:", StringComparison.Ordinal));
            var fields = uidLine?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields is not { Length: >= 2 } || !uint.TryParse(fields[1], out var uid) || uid != _uid) return null;
            return ParseStat(File.ReadAllText($"/proc/{pid}/stat"), _bootUs, _ticks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { return null; }
    }

    internal static ProcessEntry? ParseStat(string text, long bootUs, long ticks)
    {
        var close = text.LastIndexOf(')'); // comm can contain spaces and parentheses
        var open = text.IndexOf('(');
        if (open < 1 || close <= open || ticks <= 0 || !int.TryParse(text.AsSpan(0, open).Trim(), out var pid)) return null;
        var fields = text[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20 || !int.TryParse(fields[1], out var parent)
            || !long.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var start) || start < 0) return null;
        return new ProcessEntry(pid, parent, bootUs + start / ticks * 1_000_000 + start % ticks * 1_000_000 / ticks);
    }

    private bool Same(in ProcessEntry entry) => Entry(entry.Pid)?.Identity == entry.Identity;
    private string? Link(in ProcessEntry entry, string name)
    {
        try { var path = File.ResolveLinkTarget($"/proc/{entry.Pid}/{name}", false)?.FullName; return Same(entry) ? path : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    public string? ExecutablePath(in ProcessEntry entry) => Link(entry, "exe");
    public string? WorkingDirectory(in ProcessEntry entry) => Link(entry, "cwd");
    public string? BundleId(in ProcessEntry entry) => null;
    public IReadOnlyList<string>? Arguments(in ProcessEntry entry)
    {
        try
        {
            var bytes = File.ReadAllBytes($"/proc/{entry.Pid}/cmdline");
            if (bytes.Length > 65_536 || !Same(entry)) return null;
            return System.Text.Encoding.UTF8.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    public IReadOnlyCollection<string> OpenFiles(in ProcessEntry entry, long nowMs)
    {
        var paths = new List<string>();
        try
        {
            foreach (var fd in Directory.EnumerateFiles($"/proc/{entry.Pid}/fd").Take(4096))
            {
                try { if (File.ResolveLinkTarget(fd, false)?.FullName is { } path && path.StartsWith('/')) paths.Add(path); }
                catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Same(entry) ? paths : [];
    }
    private static long ReadBootUs()
    {
        var line = File.ReadLines("/proc/stat").First(l => l.StartsWith("btime ", StringComparison.Ordinal));
        return long.Parse(line.AsSpan(6), CultureInfo.InvariantCulture) * 1_000_000;
    }
    [LibraryImport("libc", EntryPoint = "getuid")] private static partial uint GetUid();
    [LibraryImport("libc", EntryPoint = "sysconf")] private static partial long Sysconf(int name);
}
