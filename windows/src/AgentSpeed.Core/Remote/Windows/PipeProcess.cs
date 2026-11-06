using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static AgentSpeed.Core.Remote.Windows.PipeProcessNative;

namespace AgentSpeed.Core.Remote.Windows;

/// <summary>
/// A process on three overlapped named pipes, started with <c>CreateProcessW</c> (REMOTE 4.1, Windows). Windows' OpenSSH
/// client stalls on synchronous pipes such as the anonymous ones <see cref="System.Diagnostics.Process"/> creates (its
/// output stops after the first write and it never exits), so both ends of each pipe are overlapped, as its own
/// <c>sshd</c> gives them. The child inherits exactly its three pipe ends (<c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>), gets
/// no window and no shell; the argument vector is quoted with the rules the C runtime parses.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PipeProcess : IRemoteProcess
{
    private const int BufferBytes = 64 * 1024;

    private readonly SafeProcessHandle _process;
    private readonly FileStream _stdin;
    private readonly RemoteQueue _writes = new("remote.stdin");
    private bool _inputOpen = true;
    private int _exited;

    private PipeProcess(SafeProcessHandle process, FileStream stdin)
    {
        _process = process;
        _stdin = stdin;
    }

    public static unsafe PipeProcess Start(RemoteProcessRequest request, RemoteProcessHandlers handlers)
    {
        var name = $@"\\.\pipe\agent-speed-remote-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var (stdinServer, stdinChild) = Pipe(name + "-in", childReads: true);
        var (stdoutServer, stdoutChild) = Pipe(name + "-out", childReads: false);
        var (stderrServer, stderrChild) = Pipe(name + "-err", childReads: false);
        try
        {
            var process = Create(request, stdinChild, stdoutChild, stderrChild);
            var started = new PipeProcess(process, new FileStream(stdinServer, FileAccess.Write, 1, isAsync: true));
            var stdout = Pump(new FileStream(stdoutServer, FileAccess.Read, 1, isAsync: true), handlers.Stdout);
            var stderr = Pump(new FileStream(stderrServer, FileAccess.Read, 1, isAsync: true), handlers.Stderr);
            _ = Task.WhenAll(stdout, stderr, started.WaitForExit()).ContinueWith(
                _ => handlers.Exit(started.ExitCode()), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return started;
        }
        catch
        {
            stdinServer.Dispose();
            stdoutServer.Dispose();
            stderrServer.Dispose();
            throw;
        }
        finally
        {
            stdinChild.Dispose();
            stdoutChild.Dispose();
            stderrChild.Dispose();
        }
    }

    public void Write(byte[] data, Action<bool>? completion = null) => _writes.Post(() =>
    {
        if (!_inputOpen)
        {
            completion?.Invoke(false);
            return;
        }

        bool written;
        try
        {
            _stdin.Write(data);
            _stdin.Flush();
            written = true;
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            written = false;
        }

        completion?.Invoke(written);
    });

    public void CloseInput() => _writes.Post(() =>
    {
        if (!_inputOpen)
        {
            return;
        }

        _inputOpen = false;
        try
        {
            _stdin.Dispose();
        }
        catch (IOException)
        {
        }
    });

    public void Terminate() => CloseInput();

    public void Kill()
    {
        if (Volatile.Read(ref _exited) == 0 && !_process.IsClosed)
        {
            TerminateProcess(_process, 1);
        }
    }

    /// <summary>A pipe with both ends overlapped; the child end is inheritable.</summary>
    private static unsafe (SafeFileHandle Server, SafeFileHandle Child) Pipe(string name, bool childReads)
    {
        var server = CreateNamedPipe(
            name,
            (childReads ? PipeAccessOutbound : PipeAccessInbound) | FileFlagOverlapped | FileFlagFirstPipeInstance,
            PipeRejectRemoteClients,
            1,
            BufferBytes,
            BufferBytes,
            0,
            0);
        if (server.IsInvalid)
        {
            throw new IOException("CreateNamedPipe failed", Marshal.GetLastPInvokeError());
        }

        var security = new SecurityAttributes { Length = (uint)sizeof(SecurityAttributes), InheritHandle = 1 };
        var access = childReads ? GenericRead | FileWriteAttributes : GenericWrite | FileReadAttributes;
        var child = CreateFile(name, access, 0, &security, OpenExisting, FileFlagOverlapped, 0);
        if (child.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            server.Dispose();
            throw new IOException("Opening the child end of a pipe failed", error);
        }

        return (server, child);
    }

    private static unsafe SafeProcessHandle Create(RemoteProcessRequest request, SafeFileHandle stdin, SafeFileHandle stdout, SafeFileHandle stderr)
    {
        var handles = stackalloc nint[3];
        handles[0] = stdin.DangerousGetHandle();
        handles[1] = stdout.DangerousGetHandle();
        handles[2] = stderr.DangerousGetHandle();
        nuint size = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref size);
        var list = Marshal.AllocHGlobal((nint)size);
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
            {
                throw new IOException("InitializeProcThreadAttributeList failed", Marshal.GetLastPInvokeError());
            }

            try
            {
                if (!UpdateProcThreadAttribute(list, 0, ProcThreadAttributeHandleList, (nint)handles, (nuint)(3 * sizeof(nint)), 0, 0))
                {
                    throw new IOException("UpdateProcThreadAttribute failed", Marshal.GetLastPInvokeError());
                }

                var info = new StartupInfoEx { AttributeList = list };
                info.StartupInfo.Size = (uint)sizeof(StartupInfoEx);
                info.StartupInfo.Flags = StartfUseStdHandles;
                info.StartupInfo.StdInput = handles[0];
                info.StartupInfo.StdOutput = handles[1];
                info.StartupInfo.StdError = handles[2];
                var commandLine = (WindowsCommandLine.Of(request.Executable, request.Arguments) + "\0").ToCharArray();
                var environment = EnvironmentBlock(request.Environment).ToCharArray();
                ProcessInformation process;
                fixed (char* line = commandLine)
                fixed (char* block = environment)
                {
                    if (!CreateProcess(request.Executable, line, 0, 0, true, CreateNoWindow | CreateUnicodeEnvironment | ExtendedStartupInfoPresent, block,
                            null, &info, out process))
                    {
                        throw new IOException("CreateProcess failed", Marshal.GetLastPInvokeError());
                    }
                }

                CloseHandle(process.Thread);
                return new SafeProcessHandle(process.Process, ownsHandle: true);
            }
            finally
            {
                DeleteProcThreadAttributeList(list);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(list);
        }
    }

    /// <summary><c>name=value\0</c> … <c>\0</c>, sorted by name without case as Windows expects.</summary>
    private static string EnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var block = new StringBuilder();
        foreach (var (name, value) in environment.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (name.Length == 0 || name.Contains('=', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal)
                || value.Contains('\0', StringComparison.Ordinal))
            {
                continue;
            }

            block.Append(name).Append('=').Append(value).Append('\0');
        }

        return block.Append('\0').ToString();
    }

    private Task WaitForExit()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = new ProcessWait(_process);
        RegisteredWaitHandle? registration = null;
        registration = ThreadPool.RegisterWaitForSingleObject(wait, (_, _) =>
        {
            Volatile.Write(ref _exited, 1);
            registration?.Unregister(null);
            wait.Dispose();
            done.TrySetResult();
        }, null, Timeout.Infinite, executeOnlyOnce: true);
        return done.Task;
    }

    private int ExitCode()
    {
        var code = GetExitCodeProcess(_process, out var value) ? unchecked((int)value) : -1;
        _process.Dispose();
        return code;
    }

    private static async Task Pump(FileStream stream, Action<byte[]> deliver)
    {
        var buffer = new byte[BufferBytes];
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                while (true)
                {
                    var count = await stream.ReadAsync(buffer).ConfigureAwait(false);
                    if (count == 0)
                    {
                        return;
                    }

                    deliver(buffer.AsSpan(0, count).ToArray());
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>The process handle as a wait handle, without owning it.</summary>
    private sealed class ProcessWait : WaitHandle
    {
        public ProcessWait(SafeProcessHandle process)
        {
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
        }
    }
}
