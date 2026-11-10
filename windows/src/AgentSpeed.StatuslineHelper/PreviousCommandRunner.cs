using System.ComponentModel;
using System.Diagnostics;

namespace AgentSpeed.StatuslineHelper;

/// <summary>The shell that runs the previous status line command (QUOTAS 5.5 step 3).</summary>
internal readonly record struct StatuslineShell(string Executable, bool IsCmd);

/// <summary>
/// Runs the user's previous status line command with the same input, environment and working folder, passes its standard
/// output through and returns its exit code; it is killed after 10 s.
/// </summary>
internal static class PreviousCommandRunner
{
    public const int TimeoutMs = 10_000;

    /// <summary>
    /// Unix: <c>/bin/sh</c>. Windows: the Git Bash Claude Code uses — <c>CLAUDE_CODE_GIT_BASH_PATH</c>, else <c>bash.exe</c> in
    /// <c>..\bin</c> of the <c>git.exe</c> on the path — else <c>cmd.exe</c>.
    /// </summary>
    public static StatuslineShell Shell(Func<string, string?> environment, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        if (!OperatingSystem.IsWindows())
        {
            return new StatuslineShell("/bin/sh", IsCmd: false);
        }

        if (environment("CLAUDE_CODE_GIT_BASH_PATH") is { Length: > 0 } configured && exists(configured))
        {
            return new StatuslineShell(configured, IsCmd: false);
        }

        foreach (var folder in (environment("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string git;
            try
            {
                git = Path.Combine(folder.Trim('"'), "git.exe");
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (!exists(git))
            {
                continue;
            }

            var bash = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(git)!, "..", "bin", "bash.exe"));
            if (exists(bash))
            {
                return new StatuslineShell(bash, IsCmd: false);
            }
        }

        var comspec = environment("ComSpec");
        return new StatuslineShell(comspec is { Length: > 0 } && exists(comspec) ? comspec : "cmd.exe", IsCmd: true);
    }

    public static ProcessStartInfo StartInfo(StatuslineShell shell, string command)
    {
        var info = new ProcessStartInfo(shell.Executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };
        if (shell.IsCmd)
        {
            info.Arguments = "/d /s /c \"" + command + "\"";
        }
        else
        {
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(command);
        }

        return info;
    }

    public static int Run(string command, byte[] input, Stream output, Func<string, string?> environment)
    {
        Process? process;
        try
        {
            process = Process.Start(StartInfo(Shell(environment), command));
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return 0;
        }

        if (process is null)
        {
            return 0;
        }

        using (process)
        {
            var copy = Task.Run(() =>
            {
                try
                {
                    process.StandardOutput.BaseStream.CopyTo(output);
                    output.Flush();
                }
                catch (IOException)
                {
                    // Either side closed.
                }
            });
            var feed = Task.Run(() =>
            {
                try
                {
                    process.StandardInput.BaseStream.Write(input);
                    process.StandardInput.Close();
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                    // The command does not read its input.
                }
            });

            if (!process.WaitForExit(TimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException)
                {
                    // It exited meanwhile.
                }

                process.WaitForExit(2_000);
            }

            Task.WaitAll([copy, feed], 2_000);
            return process.HasExited ? process.ExitCode : 0;
        }
    }
}
