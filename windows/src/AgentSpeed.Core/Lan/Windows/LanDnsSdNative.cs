using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AgentSpeed.Core.Lan.Windows;

/// <summary>The DNS-SD registration entry points of <c>dnsapi.dll</c> (Windows 10 1809 and later).</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class LanDnsSdNative
{
    public const uint QueryRequestVersion1 = 1;
    public const uint RequestPending = 9506;

    /// <summary><c>DNS_SERVICE_REGISTER_REQUEST</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public nint ServiceInstance;
        public delegate* unmanaged<uint, nint, nint, void> Completion;
        public nint Context;
        public nint Credentials;
        public int UnicastEnabled;
    }

    [LibraryImport("dnsapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint DnsServiceConstructInstance(string serviceName, string hostName, nint ip4, nint ip6, ushort port, ushort priority,
        ushort weight, uint propertyCount, nint* keys, nint* values);

    [LibraryImport("dnsapi.dll")]
    public static partial uint DnsServiceRegister(RegisterRequest* request, nint cancel);

    [LibraryImport("dnsapi.dll")]
    public static partial uint DnsServiceDeRegister(RegisterRequest* request, nint cancel);

    [LibraryImport("dnsapi.dll")]
    public static partial void DnsServiceFreeInstance(nint instance);
}
