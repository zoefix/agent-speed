using System.ComponentModel;
using System.Diagnostics;

namespace AgentSpeed.Linux.Platform;

internal readonly record struct LinuxAccessibilitySnapshot(bool ReduceMotion, bool HighContrast, bool PrefersDark)
{
    // If GNOME is unavailable, favor reduced motion and the familiar light appearance until preferences are read.
    internal static LinuxAccessibilitySnapshot SafeFallback => new(ReduceMotion: true, HighContrast: false, PrefersDark: false);
}

internal readonly record struct LinuxAccessibilityRead(bool? ReduceMotion, bool? HighContrast, bool? PrefersDark)
{
    internal static LinuxAccessibilityRead Unavailable => new(null, null, null);
}

/// <summary>Reads GNOME preferences asynchronously and keeps the last usable values if a query fails.</summary>
internal sealed class LinuxAccessibilityState
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);
    private readonly Func<CancellationToken, Task<LinuxAccessibilityRead>> _reader;
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();
    private LinuxAccessibilitySnapshot _current;
    private Task<LinuxAccessibilityRead>? _inFlight;

    internal LinuxAccessibilityState(
        Func<CancellationToken, Task<LinuxAccessibilityRead>> reader,
        TimeSpan? timeout = null,
        LinuxAccessibilitySnapshot? initial = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        _current = initial ?? LinuxAccessibilitySnapshot.SafeFallback;
    }

    internal static LinuxAccessibilityState Create(
        bool preview,
        Func<CancellationToken, Task<LinuxAccessibilityRead>> reader,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new LinuxAccessibilityState(preview ? _ => Task.FromResult(LinuxAccessibilityRead.Unavailable) : reader, timeout);
    }

    internal LinuxAccessibilitySnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Returns immediately when another query is still running; slow readers are bounded by <see cref="_timeout"/>.</summary>
    internal async Task<bool> RefreshAsync()
    {
        Task<LinuxAccessibilityRead> readTask;
        CancellationTokenSource deadline;
        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false }) return false;
            deadline = new CancellationTokenSource();
            try { readTask = _reader(deadline.Token); }
            catch { deadline.Dispose(); return false; }
            if (readTask is null) { deadline.Dispose(); return false; }
            _inFlight = readTask;
        }

        try
        {
            var read = await readTask.WaitAsync(_timeout);
            lock (_gate)
            {
                var previous = _current;
                var next = new LinuxAccessibilitySnapshot(
                    read.ReduceMotion ?? previous.ReduceMotion,
                    read.HighContrast ?? previous.HighContrast,
                    read.PrefersDark ?? previous.PrefersDark);
                if (next == previous) return false;
                _current = next;
                return true;
            }
        }
        catch (TimeoutException)
        {
            deadline.Cancel();
            ObserveFailure(readTask);
            return false;
        }
        catch
        {
            deadline.Cancel();
            ObserveFailure(readTask);
            return false;
        }
        finally
        {
            lock (_gate)
                if (readTask.IsCompleted && ReferenceEquals(_inFlight, readTask)) _inFlight = null;
            deadline.Dispose();
        }
    }

    private static void ObserveFailure(Task task) => _ = task.ContinueWith(
        static failed => { _ = failed.Exception; },
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
}

internal static class LinuxAccessibility
{
    internal static bool ResolveDark(string? scheme, string? theme) => scheme?.Trim(' ', '\'', '"') switch
    {
        "prefer-dark" => true,
        "prefer-light" => false,
        _ => theme?.Contains("dark", StringComparison.OrdinalIgnoreCase) == true,
    };

    internal static Task<LinuxAccessibilityRead> ReadGnomeAsync(CancellationToken cancellationToken) =>
        Task.Run(() => ReadGnomeCoreAsync(cancellationToken), cancellationToken);

    private static async Task<LinuxAccessibilityRead> ReadGnomeCoreAsync(CancellationToken cancellationToken)
    {
        var animations = ReadKeyAsync("enable-animations", cancellationToken);
        var theme = ReadKeyAsync("gtk-theme", cancellationToken);
        var scheme = ReadKeyAsync("color-scheme", cancellationToken);
        await Task.WhenAll(animations, theme, scheme);

        var themeValue = await theme;
        var schemeValue = await scheme;
        return new LinuxAccessibilityRead(
            ParseAnimations(animations.Result),
            themeValue is null ? null : themeValue.Contains("HighContrast", StringComparison.Ordinal),
            ParseDark(schemeValue, themeValue));
    }

    private static bool? ParseAnimations(string? value) => value?.Trim(' ', '\'', '"') switch
    {
        "false" => true,
        "true" => false,
        _ => null,
    };

    private static bool? ParseDark(string? scheme, string? theme)
    {
        var normalizedScheme = scheme?.Trim(' ', '\'', '"');
        if (normalizedScheme == "prefer-dark") return true;
        if (normalizedScheme == "prefer-light") return false;
        return theme is null ? null : theme.Contains("dark", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> ReadKeyAsync(string key, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("gsettings")
            {
                ArgumentList = { "get", "org.gnome.desktop.interface", key },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        try
        {
            if (!process.Start()) return null;
            stdout = process.StandardOutput.ReadToEndAsync();
            stderr = process.StandardError.ReadToEndAsync();
            using var registration = cancellationToken.Register(static state => Kill((Process)state!), process);
            await process.WaitForExitAsync(cancellationToken);
            await stderr;
            var output = await stdout;
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (OperationCanceledException)
        {
            await StopAsync(process, stdout, stderr);
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            await StopAsync(process, stdout, stderr);
            return null;
        }
    }

    private static async Task StopAsync(Process process, Task<string>? stdout, Task<string>? stderr)
    {
        Kill(process);
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(250)); } catch { }
        if (stdout is not null) try { await stdout.WaitAsync(TimeSpan.FromMilliseconds(250)); } catch { }
        if (stderr is not null) try { await stderr.WaitAsync(TimeSpan.FromMilliseconds(250)); } catch { }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}
