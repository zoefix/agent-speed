using System.Diagnostics;
using System.Text;

namespace AgentSpeed.Core.Remote;

/// <summary>One program to run: always the system <c>ssh</c> with an argument vector, never a shell (REMOTE 4.1).</summary>
internal sealed record RemoteProcessRequest(string Executable, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment);

/// <summary>
/// Callbacks of a running process, called on any thread. <see cref="Exit"/> comes last: after stdout and stderr reached
/// their end and the process ended.
/// </summary>
internal sealed record RemoteProcessHandlers(Action<byte[]> Stdout, Action<byte[]> Stderr, Action<int> Exit);

/// <summary>A running process. Writes go to its stdin in order, off the caller's thread.</summary>
internal interface IRemoteProcess
{
    /// <summary>Queues <paramref name="data"/>; <paramref name="completion"/> (any thread) says whether all of it was written. Ignored after <see cref="CloseInput"/>.</summary>
    void Write(byte[] data, Action<bool>? completion = null);

    /// <summary>Closes stdin after the queued writes (end of file for the remote side).</summary>
    void CloseInput();

    /// <summary>Asks the process to end. Windows has no SIGTERM: stdin closes, which ends the relay and with it <c>ssh</c>.</summary>
    void Terminate();

    /// <summary>Ends the process at once (TerminateProcess).</summary>
    void Kill();
}

/// <summary>Starts processes; tests replace it with a scripted relay.</summary>
internal interface IRemoteTransport
{
    /// <summary>The executable exists (<c>sshMissing</c> otherwise).</summary>
    bool IsAvailable(string executable);

    IRemoteProcess Launch(RemoteProcessRequest request, RemoteProcessHandlers handlers);
}

/// <summary>
/// The system process launcher: on Windows <see cref="Windows.PipeProcess"/> (named pipes, which Windows' OpenSSH needs),
/// elsewhere <see cref="Process"/> with three redirected pipes; no window and no shell either way.
/// </summary>
internal sealed class ProcessRemoteTransport : IRemoteTransport
{
    public static ProcessRemoteTransport Instance { get; } = new();

    public bool IsAvailable(string executable) => File.Exists(executable);

    public IRemoteProcess Launch(RemoteProcessRequest request, RemoteProcessHandlers handlers) =>
        OperatingSystem.IsWindows() ? Windows.PipeProcess.Start(request, handlers) : new PipedProcess(request, handlers);

    private sealed class PipedProcess : IRemoteProcess
    {
        private const int ReadBufferBytes = 64 * 1024;

        private readonly Process _process;
        private readonly RemoteQueue _writes = new("remote.stdin");
        private bool _inputOpen = true;

        public PipedProcess(RemoteProcessRequest request, RemoteProcessHandlers handlers)
        {
            var info = new ProcessStartInfo(request.Executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
            };
            foreach (var argument in request.Arguments)
            {
                info.ArgumentList.Add(argument);
            }

            info.Environment.Clear();
            foreach (var (key, value) in request.Environment)
            {
                info.Environment[key] = value;
            }

            _process = new Process { StartInfo = info };
            if (!_process.Start())
            {
                _process.Dispose();
                throw new InvalidOperationException("The process did not start.");
            }

            var stdout = Pump(_process.StandardOutput.BaseStream, handlers.Stdout);
            var stderr = Pump(_process.StandardError.BaseStream, handlers.Stderr);
            var process = _process;
            _ = Task.WhenAll(stdout, stderr).ContinueWith(
                _ =>
                {
                    var status = -1;
                    try
                    {
                        process.WaitForExit();
                        status = process.ExitCode;
                    }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                    }
                    finally
                    {
                        process.Dispose();
                    }

                    handlers.Exit(status);
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
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
                var stream = _process.StandardInput.BaseStream;
                stream.Write(data);
                stream.Flush();
                written = true;
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
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
                _process.StandardInput.Close();
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
        });

        public void Terminate() => CloseInput();

        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                }
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }
        }

        private static async Task Pump(Stream stream, Action<byte[]> deliver)
        {
            var buffer = new byte[ReadBufferBytes];
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
}
