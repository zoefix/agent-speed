using System.Text;
using System.Text.Json.Nodes;

namespace AgentSpeed.Core.Quota;

/// <summary>Where the bridge reads and writes (QUOTAS 5.1): Claude Code's user settings and the app's data folder.</summary>
public sealed record ClaudeCliPaths(string SettingsFile, string DataFolder)
{
    public string BinFolder => Path.Combine(DataFolder, "bin");

    public string QuotaFolder => Path.Combine(DataFolder, ClaudeCliQuota.QuotaFolder);

    public string HelperCopy => Path.Combine(BinFolder, ClaudeCliQuota.HelperName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));

    public string QuotaFile => Path.Combine(QuotaFolder, ClaudeCliQuota.QuotaFileName);

    public string Sidecar => Path.Combine(QuotaFolder, ClaudeCliQuota.SidecarFileName);

    public string Backup => Path.Combine(QuotaFolder, ClaudeCliQuota.BackupFileName);

    /// <summary><c>$CLAUDE_CONFIG_DIR/settings.json</c>, else <c>~/.claude/settings.json</c>; data in <c>%LOCALAPPDATA%\AgentSpeed</c>.</summary>
    public static ClaudeCliPaths Default(Func<string, string?>? environment = null, string? dataFolder = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var config = environment("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        dataFolder ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentSpeed");
        return new ClaudeCliPaths(Path.Combine(config, "settings.json"), dataFolder);
    }
}

/// <summary>
/// The Claude CLI status line bridge on disk (QUOTAS 5.2–5.4): installs the helper copy, edits Claude Code's settings
/// with a sidecar and a backup, restores them, and reports the status. Never reads anything else of Claude Code's.
/// </summary>
public sealed class ClaudeCliBridge
{
    private readonly ClaudeCliPaths _paths;
    private readonly Func<Stream?> _helper;

    /// <param name="helper">Opens the helper executable shipped with the app; null when the app carries none.</param>
    public ClaudeCliBridge(ClaudeCliPaths paths, Func<Stream?> helper)
    {
        _paths = paths;
        _helper = helper;
    }

    public ClaudeCliPaths Paths => _paths;

    /// <summary>The <c>command</c> value the settings get on this platform.</summary>
    public string Command => OperatingSystem.IsWindows()
        ? ClaudeCliSettings.WindowsCommand(_paths.HelperCopy)
        : ClaudeCliSettings.PosixCommand(_paths.HelperCopy);

    /// <summary>Copies the helper to <c>&lt;data&gt;/bin</c> when the bytes differ (temporary file, then rename); false without a helper.</summary>
    public bool EnsureHelper()
    {
        using var source = _helper();
        if (source is null)
        {
            return false;
        }

        using var copy = new MemoryStream();
        source.CopyTo(copy);
        var bytes = copy.ToArray();
        if (bytes.Length == 0)
        {
            return false;
        }

        try
        {
            if (File.Exists(_paths.HelperCopy) && new FileInfo(_paths.HelperCopy).Length == bytes.Length
                && File.ReadAllBytes(_paths.HelperCopy).AsSpan().SequenceEqual(bytes))
            {
                return true;
            }

            Directory.CreateDirectory(_paths.BinFolder);
            var temp = _paths.HelperCopy + "." + Environment.ProcessId + ".tmp";
            File.WriteAllBytes(temp, bytes);
            try
            {
                File.Move(temp, _paths.HelperCopy, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                TryDelete(temp);
                // A copy that is running right now cannot be replaced; an older copy still works.
                return File.Exists(_paths.HelperCopy);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Turning the bridge on (5.2); returns the status, <see cref="ClaudeCliBridgeStatus.Failed"/> when nothing could be written.</summary>
    public ClaudeCliBridgeStatus TurnOn()
    {
        if (!EnsureHelper())
        {
            return ClaudeCliBridgeStatus.Failed;
        }

        string? text;
        try
        {
            text = ReadText(_paths.SettingsFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ClaudeCliBridgeStatus.Unreadable;
        }

        var sidecar = ReadSidecar();
        var on = ClaudeCliSettings.On(text, Command, sidecar);
        if (on.Settings is null)
        {
            return on.Status;
        }

        var wroteSidecar = false;
        try
        {
            Directory.CreateDirectory(_paths.QuotaFolder);
            if (!ReferenceEquals(on.Previous, sidecar) && on.Previous is not null)
            {
                WriteAtomically(_paths.Sidecar, ClaudeCliSettings.SidecarText(on.Previous));
                wroteSidecar = true;
                if (text is not null)
                {
                    WriteAtomically(_paths.Backup, text);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_paths.SettingsFile)!);
            WriteAtomically(_paths.SettingsFile, on.Settings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (wroteSidecar && sidecar is null)
            {
                TryDelete(_paths.Sidecar);
                TryDelete(_paths.Backup);
            }

            return ClaudeCliBridgeStatus.Failed;
        }

        return Status();
    }

    /// <summary>Turning the bridge off (5.3): restores the previous value while ours, then deletes the sidecar, backup and quota file.</summary>
    public void TurnOff()
    {
        try
        {
            var text = ReadText(_paths.SettingsFile);
            if (ClaudeCliSettings.Off(text, ReadSidecar()) is { } restored)
            {
                WriteAtomically(_paths.SettingsFile, restored);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The user's settings stay as they are; our own files still go.
        }

        TryDelete(_paths.Sidecar);
        TryDelete(_paths.Backup);
        TryDelete(_paths.QuotaFile);
    }

    /// <summary>The bridge's status as the settings file shows it (5.4).</summary>
    public ClaudeCliBridgeStatus Status()
    {
        try
        {
            return ClaudeCliSettings.Status(ReadText(_paths.SettingsFile), File.Exists(_paths.QuotaFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ClaudeCliBridgeStatus.Unreadable;
        }
    }

    private JsonObject? ReadSidecar()
    {
        try
        {
            return ClaudeCliSettings.ReadSidecar(ReadText(_paths.Sidecar));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadText(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>A temporary file beside the target, then a rename that keeps the target's permissions.</summary>
    internal static void WriteAtomically(string path, string text)
    {
        var temp = path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        try
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    // Replace keeps the permissions; a plain rename still writes the file atomically.
                }
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
