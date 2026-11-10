using System.Text;
using AgentSpeed.Core.Quota;

namespace AgentSpeed.StatuslineHelper;

/// <summary>
/// One run of the helper (QUOTAS 5.5): read the status line input, merge its <c>rate_limits</c> into the quota file, then
/// run the previous status line command with the same input or print the quota line. Never prints anything else and
/// never fails on its own errors.
/// </summary>
internal sealed class StatuslineRun
{
    /// <summary>Input beyond this is still passed to the previous command, but not parsed.</summary>
    public const int ForwardLimit = 16 << 20;

    private readonly string _dataFolder;
    private readonly Func<long> _nowMs;
    private readonly Func<string, string?> _environment;

    public StatuslineRun(string dataFolder, Func<long>? nowMs = null, Func<string, string?>? environment = null)
    {
        _dataFolder = dataFolder;
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    private string QuotaFolder => Path.Combine(_dataFolder, ClaudeCliQuota.QuotaFolder);

    /// <summary><c>&lt;data&gt;</c> for an executable in <c>&lt;data&gt;/bin</c>; else the app's data folder.</summary>
    public static string DataFolderFor(string? executable)
    {
        var folder = Path.GetDirectoryName(executable ?? string.Empty);
        if (!string.IsNullOrEmpty(folder) && string.Equals(Path.GetFileName(folder), "bin", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(folder) is { Length: > 0 } data)
        {
            return data;
        }

        return OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentSpeed")
            : OperatingSystem.IsMacOS()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "AgentSpeed")
                : Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
                    ? xdg
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"), "agent-speed");
    }

    public int Run(Stream input, Stream output)
    {
        byte[] bytes;
        try
        {
            bytes = ReadAtMost(input, ForwardLimit);
        }
        catch (IOException)
        {
            bytes = [];
        }

        try
        {
            if (bytes.Length <= ClaudeCliQuota.InputLimit)
            {
                Merge(bytes);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The quota is lost for this response only; the status line still shows.
        }

        if (PreviousCommand() is { } command)
        {
            return PreviousCommandRunner.Run(command, bytes, output, _environment);
        }

        try
        {
            var line = ClaudeCliQuota.Line(Stored(), _nowMs());
            if (line.Length > 0)
            {
                output.Write(Encoding.UTF8.GetBytes(line + "\n"));
                output.Flush();
            }
        }
        catch (IOException)
        {
            // Claude Code went away.
        }

        return 0;
    }

    private CliQuotaState Stored()
    {
        var file = Path.Combine(QuotaFolder, ClaudeCliQuota.QuotaFileName);
        try
        {
            return File.Exists(file) ? ClaudeCliQuota.Read(File.ReadAllBytes(file)) : CliQuotaState.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return CliQuotaState.Empty;
        }
    }

    private string? PreviousCommand()
    {
        var sidecar = Path.Combine(QuotaFolder, ClaudeCliQuota.SidecarFileName);
        try
        {
            return File.Exists(sidecar) ? ClaudeCliQuota.PreviousCommand(File.ReadAllBytes(sidecar)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Merges under a lock file, so two sessions' helpers never lose each other's values; writes only a change.</summary>
    private void Merge(byte[] input)
    {
        if (!HasRateLimits(input))
        {
            return;
        }

        Directory.CreateDirectory(QuotaFolder);
        using var gate = Lock(Path.Combine(QuotaFolder, "claude-cli.lock"));
        var file = Path.Combine(QuotaFolder, ClaudeCliQuota.QuotaFileName);
        var stored = File.Exists(file) ? ClaudeCliQuota.Read(File.ReadAllBytes(file)) : CliQuotaState.Empty;
        var merged = ClaudeCliQuota.Merge(stored, input, _nowMs(), out var changed);
        if (!changed)
        {
            return;
        }

        var temp = file + "." + Environment.ProcessId + ".tmp";
        File.WriteAllBytes(temp, ClaudeCliQuota.Write(merged));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, file, overwrite: true);
                return;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < 5)
            {
                Thread.Sleep(20);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                File.Delete(temp);
                throw;
            }
        }
    }

    private static bool HasRateLimits(byte[] input) => input.AsSpan().IndexOf("\"rate_limits\""u8) >= 0;

    private static FileStream? Lock(string path)
    {
        for (var attempt = 0; attempt < 25; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException)
            {
                Thread.Sleep(10);
            }
        }

        return null;
    }

    private static byte[] ReadAtMost(Stream input, int limit)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while (buffer.Length < limit && (read = input.Read(chunk, 0, (int)Math.Min(chunk.Length, limit - buffer.Length))) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
