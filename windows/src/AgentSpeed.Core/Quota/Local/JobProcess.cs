using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AgentSpeed.Core.Remote.Windows;
using Microsoft.Win32.SafeHandles;
using static AgentSpeed.Core.Quota.Local.JobProcessNative;
using static AgentSpeed.Core.Remote.Windows.PipeProcessNative;

namespace AgentSpeed.Core.Quota.Local;

/// <summary>How a <see cref="JobProcess"/> run ended.</summary>
internal enum JobProcessEnd
{
    Exited,
    TimedOut,

    /// <summary>Standard output went past its cap: the process was killed.</summary>
    Overflow,

    /// <summary>It could not be started, or the wait was cancelled.</summary>
    Failed,
}

/// <summary>What a run printed (up to its cap) and when its process ended (epoch ms).</summary>
internal sealed record JobProcessResult(JobProcessEnd End, byte[] Output, long EndedMs);

/// <summary>
/// One run of an executable in a job object that kills its processes when closed (QUOTAS 9.4 <i>Run</i>): started
/// suspended, put in the job, then resumed, so no descendant escapes it. Standard input and standard error are the null
/// device; standard output is read up to a cap from a pipe whose child end is synchronous; the child inherits exactly
/// those three handles. Once the process ends, is killed after its time limit or prints too much, the whole job is
/// terminated, so no descendant stays.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class JobProcess
{
    private const int BufferBytes = 64 * 1024;
    private const int ReadGraceMs = 5_000;
    private const int DrainMs = 5_000;

    public static async Task<JobProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
        string directory, int timeoutMs, int capBytes, Func<long> now, CancellationToken cancellationToken)
    {
        var job = CreateJob();
        if (job == 0)
        {
            return new JobProcessResult(JobProcessEnd.Failed, [], now());
        }

        try
        {
            return await RunInJobAsync(job, executable, arguments, environment, directory, timeoutMs, capBytes, now, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TerminateJobObject(job, 1);
            CloseHandle(job);
        }
    }

    private static async Task<JobProcessResult> RunInJobAsync(nint job, string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, string directory, int timeoutMs, int capBytes, Func<long> now, CancellationToken cancellationToken)
    {
        SafeFileHandle server, child;
        try
        {
            (server, child) = OutputPipe();
        }
        catch (IOException)
        {
            return new JobProcessResult(JobProcessEnd.Failed, [], now());
        }

        SafeProcessHandle? process;
        try
        {
            using (child)
            using (var input = Null(GenericRead))
            using (var error = Null(GenericWrite))
            {
                process = Start(job, executable, arguments, environment, directory, input, child, error);
            }
        }
        catch (IOException)
        {
            process = null;
        }

        if (process is null)
        {
            server.Dispose();
            return new JobProcessResult(JobProcessEnd.Failed, [], now());
        }

        using (process)
        {
            using var stopReading = new CancellationTokenSource();
            var stream = new FileStream(server, FileAccess.Read, 1, isAsync: true);
            await using (stream.ConfigureAwait(false))
            {
                var reading = ReadAsync(stream, capBytes, () => TerminateJobObject(job, 1), stopReading.Token);
                var exited = await WaitAsync(process, timeoutMs, cancellationToken).ConfigureAwait(false);
                var endedMs = now();
                TerminateJobObject(job, 1);
                if (await Task.WhenAny(reading, Task.Delay(ReadGraceMs, CancellationToken.None)).ConfigureAwait(false) != reading)
                {
                    await stopReading.CancelAsync().ConfigureAwait(false);
                }

                (byte[] Output, bool Overflow) read;
                try
                {
                    read = await reading.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    read = ([], false);
                    exited = false;
                }

                await DrainAsync(job).ConfigureAwait(false);
                var end = read.Overflow ? JobProcessEnd.Overflow
                    : exited ? JobProcessEnd.Exited
                    : cancellationToken.IsCancellationRequested ? JobProcessEnd.Failed
                    : JobProcessEnd.TimedOut;
                return new JobProcessResult(end, end == JobProcessEnd.Exited ? read.Output : [], endedMs);
            }
        }
    }

    /// <summary>A job that kills every process in it once its last handle closes (also when the app itself ends).</summary>
    private static unsafe nint CreateJob()
    {
        var job = CreateJobObject(0, null);
        if (job == 0)
        {
            return 0;
        }

        var limits = new ExtendedLimitInformation
        {
            BasicLimitInformation = new BasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitDieOnUnhandledException },
        };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, (uint)sizeof(ExtendedLimitInformation)))
        {
            CloseHandle(job);
            return 0;
        }

        return job;
    }

    /// <summary>A single-instance pipe: our end overlapped (for cancellable reads), the child's synchronous and inheritable.</summary>
    private static unsafe (SafeFileHandle Server, SafeFileHandle Child) OutputPipe()
    {
        var name = $@"\\.\pipe\agent-speed-job-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var server = CreateNamedPipe(name, PipeAccessInbound | FileFlagOverlapped | FileFlagFirstPipeInstance, PipeRejectRemoteClients, 1,
            BufferBytes, BufferBytes, 0, 0);
        if (server.IsInvalid)
        {
            throw new IOException("CreateNamedPipe failed", Marshal.GetLastPInvokeError());
        }

        var security = new SecurityAttributes { Length = (uint)sizeof(SecurityAttributes), InheritHandle = 1 };
        var child = CreateFile(name, GenericWrite | FileReadAttributes, 0, &security, OpenExisting, 0, 0);
        if (child.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            server.Dispose();
            throw new IOException("Opening the child end of a pipe failed", error);
        }

        return (server, child);
    }

    /// <summary>The null device, inheritable.</summary>
    private static unsafe SafeFileHandle Null(uint access)
    {
        var security = new SecurityAttributes { Length = (uint)sizeof(SecurityAttributes), InheritHandle = 1 };
        var handle = CreateFile("NUL", access, FileShareAll, &security, OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException("Opening the null device failed", error);
        }

        return handle;
    }

    /// <summary>Started suspended, put in the job and resumed; null when any step fails (a started process is killed).</summary>
    private static unsafe SafeProcessHandle? Start(nint job, string executable, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
        string directory, SafeFileHandle input, SafeFileHandle output, SafeFileHandle error)
    {
        var handles = stackalloc nint[3];
        handles[0] = input.DangerousGetHandle();
        handles[1] = output.DangerousGetHandle();
        handles[2] = error.DangerousGetHandle();
        nuint size = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref size);
        var list = Marshal.AllocHGlobal((nint)size);
        try
        {
            if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
            {
                return null;
            }

            try
            {
                if (!UpdateProcThreadAttribute(list, 0, ProcThreadAttributeHandleList, (nint)handles, (nuint)(3 * sizeof(nint)), 0, 0))
                {
                    return null;
                }

                var info = new StartupInfoEx { AttributeList = list };
                info.StartupInfo.Size = (uint)sizeof(StartupInfoEx);
                info.StartupInfo.Flags = StartfUseStdHandles;
                info.StartupInfo.StdInput = handles[0];
                info.StartupInfo.StdOutput = handles[1];
                info.StartupInfo.StdError = handles[2];
                var commandLine = (WindowsCommandLine.Of(executable, arguments) + "\0").ToCharArray();
                var block = PipeProcess.EnvironmentBlock(environment).ToCharArray();
                ProcessInformation started;
                fixed (char* line = commandLine)
                fixed (char* variables = block)
                {
                    if (!CreateProcess(executable, line, 0, 0, true, CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment | ExtendedStartupInfoPresent,
                            variables, directory, &info, out started))
                    {
                        return null;
                    }
                }

                var process = new SafeProcessHandle(started.Process, ownsHandle: true);
                try
                {
                    if (!AssignProcessToJobObject(job, process) || ResumeThread(started.Thread) == ResumeFailed)
                    {
                        TerminateProcess(process, 1);
                        process.Dispose();
                        return null;
                    }

                    return process;
                }
                finally
                {
                    CloseHandle(started.Thread);
                }
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

    /// <summary>True when the process ended, false after the time limit or a cancellation.</summary>
    private static async Task<bool> WaitAsync(SafeProcessHandle process, int timeoutMs, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var wait = new ProcessWait(process);
        var registration = ThreadPool.RegisterWaitForSingleObject(wait, (_, timedOut) => done.TrySetResult(!timedOut), null, timeoutMs, executeOnlyOnce: true);
        try
        {
            using (cancellationToken.Register(() => done.TrySetResult(false)))
            {
                return await done.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            registration.Unregister(null);
        }
    }

    /// <summary>Everything up to <paramref name="capBytes"/>; one byte more calls <paramref name="overflow"/> and stops.</summary>
    private static async Task<(byte[] Output, bool Overflow)> ReadAsync(FileStream stream, int capBytes, Action overflow, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferBytes];
        using var output = new MemoryStream();
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                if (output.Length + count > capBytes)
                {
                    overflow();
                    return ([], true);
                }

                output.Write(buffer, 0, count);
            }
        }
        catch (IOException)
        {
            // A broken pipe ends the output.
        }

        return (output.ToArray(), false);
    }

    /// <summary>Waits until the terminated job holds no process, so its working folder can be removed.</summary>
    private static async Task DrainAsync(nint job)
    {
        var deadline = Environment.TickCount64 + DrainMs;
        while (ActiveProcesses(job) > 0 && Environment.TickCount64 < deadline)
        {
            await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static unsafe uint ActiveProcesses(nint job)
    {
        BasicAccountingInformation accounting;
        return QueryInformationJobObject(job, JobObjectBasicAccountingInformation, &accounting, (uint)sizeof(BasicAccountingInformation), out _)
            ? accounting.ActiveProcesses
            : 0;
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
