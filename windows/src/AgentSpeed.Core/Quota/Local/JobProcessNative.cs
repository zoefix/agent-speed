using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace AgentSpeed.Core.Quota.Local;

/// <summary>
/// The Win32 entry points <see cref="JobProcess"/> and <see cref="AgyBinary"/> add to the process-creation ones of
/// <see cref="Remote.Windows.PipeProcessNative"/>: job objects, a suspended start and a file's identity.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class JobProcessNative
{
    public const uint CreateSuspended = 0x00000004;
    public const uint FileShareAll = 0x00000007;
    public const uint FileTypeDisk = 0x0001;
    public const uint FileAttributeDirectory = 0x00000010;
    public const int JobObjectBasicAccountingInformation = 1;
    public const int JobObjectExtendedLimitInformation = 9;
    public const uint JobObjectLimitDieOnUnhandledException = 0x00000400;
    public const uint JobObjectLimitKillOnJobClose = 0x00002000;
    public const uint ResumeFailed = 0xFFFFFFFF;

    /// <summary><c>JOBOBJECT_BASIC_LIMIT_INFORMATION</c> (64-bit).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    /// <summary><c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    /// <summary><c>JOBOBJECT_BASIC_ACCOUNTING_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    /// <summary><c>BY_HANDLE_FILE_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateJobObject(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetInformationJobObject(nint job, int informationClass, void* information, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryInformationJobObject(nint job, int informationClass, void* information, uint length, out uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AssignProcessToJobObject(nint job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateJobObject(nint job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint GetFileType(SafeFileHandle file);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    public static partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint length, uint flags);
}
