using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AgentSpeed.Core.Quota;

/// <summary>Reads the official account/rateLimits/read RPC using Codex's existing sign-in.</summary>
public sealed class CodexQuotaClient
{
    public async Task<CodexQuotaSnapshot> ReadAsync(string? executable = null, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var token = timeout.Token;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable ?? FindExecutable(),
                Arguments = "app-server --listen stdio://",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
            },
        };
        process.StartInfo.Environment["RUST_LOG"] = "error";
        process.Start();
        var errors = process.StandardError.ReadToEndAsync(token);
        try
        {
            await SendAsync(process, """{"id":1,"method":"initialize","params":{"clientInfo":{"name":"agent-speed-quota","version":"1.0.0"},"capabilities":{"experimentalApi":true}}}""", token).ConfigureAwait(false);
            using (await ResponseAsync(process, 1, token).ConfigureAwait(false)) { }
            await SendAsync(process, """{"method":"initialized"}""", token).ConfigureAwait(false);
            await SendAsync(process, """{"id":2,"method":"account/rateLimits/read"}""", token).ConfigureAwait(false);
            using var response = await ResponseAsync(process, 2, token).ConfigureAwait(false);
            if (!response.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Codex returned no quota result.");
            }

            return CodexQuotaSnapshot.Parse(result, DateTimeOffset.UtcNow);
        }
        finally
        {
            process.StandardInput.Close();
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await process.WaitForExitAsync(shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // Only this read-only helper is terminated; existing Codex sessions are never touched.
                try { process.Kill(entireProcessTree: true); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }

            try { await errors.ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or IOException) { }
        }
    }

    public static string FindExecutable()
    {
        if (Environment.GetEnvironmentVariable("CODEX_EXE") is { Length: > 0 } configured
            && Path.IsPathFullyQualified(configured) && File.Exists(configured))
        {
            return configured;
        }

        var bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        // Desktop updates keep the actual CLI in a versioned directory. The outer launcher can require elevation;
        // use the same ordinary-user executable as the already running desktop app instead.
        foreach (var running in Process.GetProcessesByName("codex"))
        {
            using (running)
            {
                try
                {
                    if (running.MainModule?.FileName is { } active && File.Exists(active)
                        && (active.StartsWith(bin + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(Path.GetDirectoryName(active), bin, StringComparison.OrdinalIgnoreCase)
                            || active.Contains(@"\resources\", StringComparison.OrdinalIgnoreCase)
                            || active.Contains(@"\vendor\", StringComparison.OrdinalIgnoreCase)))
                    {
                        return active;
                    }
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }

        if (Directory.Exists(bin))
        {
            foreach (var directory in Directory.EnumerateDirectories(bin).OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                var candidate = Path.Combine(directory, "codex.exe");
                if (File.Exists(candidate)) { return candidate; }
            }
        }

        var bundled = Path.Combine(bin, "codex.exe");
        if (File.Exists(bundled)) { return bundled; }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(directory.Trim('"'), "codex.exe");
            if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate)) { return candidate; }
        }

        throw new FileNotFoundException("Sign in to Codex and install its local command-line executable.");
    }

    private static async Task SendAsync(Process process, string message, CancellationToken token)
    {
        await process.StandardInput.WriteLineAsync(message.AsMemory(), token).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ResponseAsync(Process process, int id, CancellationToken token)
    {
        while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0) { continue; }
            using var message = JsonDocument.Parse(line);
            if (!message.RootElement.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number
                || !responseId.TryGetInt32(out var value) || value != id)
            {
                continue;
            }

            if (message.RootElement.TryGetProperty("error", out _))
            {
                // Do not copy server diagnostics (which can contain account information) into application logs.
                throw new InvalidDataException("Codex could not read the signed-in account's quota.");
            }

            return JsonDocument.Parse(line);
        }

        throw new IOException("The Codex quota helper exited before replying.");
    }
}
