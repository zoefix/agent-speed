using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static AgentSpeed.Core.Lan.Windows.LanDnsSdNative;

namespace AgentSpeed.Core.Lan.Windows;

/// <summary>
/// The server's DNS-SD service on Windows (LAN 4.1): <c>_agentspeed._tcp</c> with the computer's name and the TXT record
/// <c>schema=1</c>, <c>id=&lt;instance&gt;</c>, registered with <c>DnsServiceRegister</c>. A failure is logged and changes nothing.
/// The request and its instance stay allocated until Windows confirms the deregistration.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class LanDnsSd : ILanAdvertiser
{
    private const string ServiceType = "._agentspeed._tcp.local";

    private static readonly ConcurrentDictionary<nint, Registration> Pending = new();
    private static long _nextId;
    private readonly object _gate = new();
    private Registration? _current;

    public void Register(string name, int port, string instance)
    {
        lock (_gate)
        {
            UnregisterLocked();
            try
            {
                _current = Registration.Start(Escape(name) + ServiceType, Dns.GetHostName() + ".local", (ushort)port, instance);
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or System.Net.Sockets.SocketException)
            {
                LanLog.Write($"DNS-SD is not available ({error.GetType().Name})");
            }
        }
    }

    public void Unregister()
    {
        lock (_gate)
        {
            UnregisterLocked();
        }
    }

    public void Dispose() => Unregister();

    /// <summary>A DNS-SD instance label in presentation form: <c>.</c> and <c>\</c> escaped.</summary>
    internal static string Escape(string name) => name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(".", "\\.", StringComparison.Ordinal);

    private void UnregisterLocked()
    {
        if (_current is { } current)
        {
            _current = null;
            current.Stop();
        }
    }

    [UnmanagedCallersOnly]
    private static void Completed(uint status, nint context, nint instance)
    {
        try
        {
            if (instance != 0)
            {
                DnsServiceFreeInstance(instance);
            }

            if (!Pending.TryGetValue(context, out var registration))
            {
                return;
            }

            if (registration.Complete() == 2)
            {
                Pending.TryRemove(context, out _);
                registration.Free();
            }
            else if (registration.Stopping)
            {
            }
            else if (status != 0)
            {
                LanLog.Write($"DNS-SD registration failed ({status})");
            }
            else
            {
                LanLog.Write("DNS-SD service registered");
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
        }
    }

    private sealed class Registration
    {
        private nint _request;
        private nint _instance;
        private readonly nint _id;
        private int _completions;

        private Registration(nint id)
        {
            _id = id;
        }

        public bool Stopping { get; private set; }

        /// <summary>Counts a callback: the registration's, then the deregistration's; the second one frees the memory.</summary>
        public int Complete() => Interlocked.Increment(ref _completions);

        public static Registration Start(string serviceName, string hostName, ushort port, string instanceId)
        {
            var registration = new Registration((nint)Interlocked.Increment(ref _nextId));
            var keys = stackalloc nint[2];
            var values = stackalloc nint[2];
            keys[0] = Marshal.StringToHGlobalUni("schema");
            keys[1] = Marshal.StringToHGlobalUni("id");
            values[0] = Marshal.StringToHGlobalUni("1");
            values[1] = Marshal.StringToHGlobalUni(instanceId);
            try
            {
                registration._instance = DnsServiceConstructInstance(serviceName, hostName, 0, 0, port, 0, 0, 2, keys, values);
            }
            finally
            {
                for (var i = 0; i < 2; i++)
                {
                    Marshal.FreeHGlobal(keys[i]);
                    Marshal.FreeHGlobal(values[i]);
                }
            }

            if (registration._instance == 0)
            {
                LanLog.Write("DNS-SD instance could not be built");
                return registration;
            }

            registration._request = Marshal.AllocHGlobal(sizeof(RegisterRequest));
            var request = (RegisterRequest*)registration._request;
            *request = new RegisterRequest
            {
                Version = QueryRequestVersion1,
                InterfaceIndex = 0,
                ServiceInstance = registration._instance,
                Completion = &Completed,
                Context = registration._id,
            };
            Pending[registration._id] = registration;
            var result = DnsServiceRegister(request, 0);
            if (result != RequestPending)
            {
                LanLog.Write($"DNS-SD registration failed ({result})");
                Pending.TryRemove(registration._id, out _);
                registration.Free();
            }

            return registration;
        }

        /// <summary>
        /// Deregisters; the memory is freed by the deregistration's callback. When Windows does not take the request, the few
        /// bytes stay allocated, since Windows may still hold the registration.
        /// </summary>
        public void Stop()
        {
            if (_request == 0)
            {
                Free();
                return;
            }

            if (!Pending.ContainsKey(_id))
            {
                return;
            }

            Stopping = true;
            var result = DnsServiceDeRegister((RegisterRequest*)_request, 0);
            if (result != RequestPending)
            {
                LanLog.Write($"DNS-SD deregistration failed ({result})");
                Pending.TryRemove(_id, out _);
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Free()
        {
            if (_instance != 0)
            {
                DnsServiceFreeInstance(_instance);
                _instance = 0;
            }

            if (_request != 0)
            {
                Marshal.FreeHGlobal(_request);
                _request = 0;
            }
        }
    }
}
