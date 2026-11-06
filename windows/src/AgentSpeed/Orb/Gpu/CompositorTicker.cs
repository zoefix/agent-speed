using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AgentSpeed.App;
using AgentSpeed.Interop;
using Vortice.DXGI;

namespace AgentSpeed.Orb.Gpu;

/// <summary>
/// The orbs' frame clock without WPF's render loop (DESIGN 2a, Frame classes: "one DXGI/DComp frame callback"): a background
/// thread waits for the compositor clock (Windows 11), else for the primary output's vertical blank, else sleeps one refresh
/// interval, and posts one coalesced message per tick to a message window on the UI thread. It runs only while some orb
/// animates; otherwise the thread blocks and nothing wakes.
/// </summary>
internal sealed unsafe class CompositorTicker : IDisposable
{
    private const string ClassName = "AgentSpeedOrbTicker";
    private const uint TickMessage = WM.APP + 0x41;
    private const uint WAIT_TIMEOUT = 0x102;
    private static CompositorTicker? s_instance;
    private static bool s_registered;

    private readonly Action<long> _tick;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly delegate* unmanaged[Stdcall]<uint, nint*, uint, uint> _waitForClock;
    private volatile bool _running;
    private volatile bool _disposed;
    private volatile int _pending;
    private volatile int _urgent = 1;
    private long _postedAt;
    private long _divisors = 1L << 1;
    private long _count;
    private nint _window;
    private nint _powerNotification;
    private double _refreshHz = 60;

    public CompositorTicker(Action<long> tick)
    {
        _tick = tick;
        s_instance = this;
        EnsureClass();
        _window = User32.CreateWindowEx(0, ClassName, null, 0, 0, 0, 0, 0, -3 /* HWND_MESSAGE */, 0, Kernel32.GetModuleHandle(null), 0);
        if (_window != 0)
        {
            _powerNotification = OrbNative.RegisterPowerSettingNotification(_window, OrbNative.ConsoleDisplayState, OrbNative.DEVICE_NOTIFY_WINDOW_HANDLE);
        }
        if (NativeLibrary.TryLoad("dcomp.dll", out var dcomp) && NativeLibrary.TryGetExport(dcomp, "DCompositionWaitForCompositorClock", out var wait))
        {
            _waitForClock = (delegate* unmanaged[Stdcall]<uint, nint*, uint, uint>)wait;
        }

        Source = _waitForClock is not null ? "compositor clock" : "vblank";
        FrameStats.Attach();
        _thread = new Thread(Run) { IsBackground = true, Name = "Orb frame clock", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>The console display is off (DESIGN 2a, Frame policy: rendering pauses entirely); data keeps flowing.</summary>
    public static bool DisplayOff { get; private set; }

    /// <summary>Raised on the UI thread when the console display turns off or back on.</summary>
    public static event EventHandler? DisplayStateChanged;

    /// <summary>What paces the ticks (for the log).</summary>
    public string Source { get; private set; }

    /// <summary>Ticks posted since start.</summary>
    public long Count => Interlocked.Read(ref _count);

    public double RefreshHz
    {
        get => Volatile.Read(ref _refreshHz);
        set => Volatile.Write(ref _refreshHz, value >= 24 ? value : 60);
    }

    public bool Running => _running;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _wake.Set();
    }

    public void Stop() => _running = false;

    /// <summary>The frame divisors in use (bit d: some orb renders on every d-th tick); other ticks do not wake the UI thread.</summary>
    public void SetDivisors(ulong mask) => Interlocked.Exchange(ref _divisors, (long)(mask == 0 ? 1UL << 1 : mask));

    /// <summary>The next tick wakes the UI thread whatever the divisors (an orb asked for a frame).</summary>
    public void Wake() => _urgent = 1;

    private bool IsDue(long count)
    {
        if (Interlocked.Exchange(ref _urgent, 0) == 1)
        {
            return true;
        }

        var mask = (ulong)Interlocked.Read(ref _divisors);
        for (var d = 1; d < 64 && mask >> d != 0; d++)
        {
            if ((mask >> d & 1) != 0 && count % d == 0)
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _running = false;
        _wake.Set();
        _thread.Join(500);
        if (_powerNotification != 0)
        {
            OrbNative.UnregisterPowerSettingNotification(_powerNotification);
            _powerNotification = 0;
        }

        if (_window != 0)
        {
            User32.DestroyWindow(_window);
            _window = 0;
        }

        if (ReferenceEquals(s_instance, this))
        {
            s_instance = null;
        }
    }

    private void Run()
    {
        IDXGIOutput? output = null;
        var vblankFailures = 0;
        var clock = Stopwatch.StartNew();
        try
        {
            while (!_disposed)
            {
                if (!_running)
                {
                    _wake.WaitOne();
                    continue;
                }

                var period = 1000.0 / RefreshHz;
                var before = clock.Elapsed.TotalMilliseconds;
                if (_waitForClock is not null)
                {
                    // A tick, or a timeout while the compositor is idle (the orb's next present wakes it up again).
                    _waitForClock(0, null, (uint)Math.Clamp(Math.Ceiling(period * 2), 8, 100));
                }
                else if (vblankFailures < 8)
                {
                    try
                    {
                        output ??= PrimaryOutput();
                        output?.WaitForVBlank();
                    }
                    catch (SharpGen.Runtime.SharpGenException)
                    {
                        output?.Dispose();
                        output = null;
                        vblankFailures++;
                    }
                }

                var elapsed = clock.Elapsed.TotalMilliseconds - before;
                if (elapsed < period * 0.4)
                {
                    // No real wait happened (display off, remote session): pace by time instead.
                    if (_waitForClock is null)
                    {
                        vblankFailures++;
                    }

                    Thread.Sleep(Math.Max(1, (int)(period - elapsed)));
                }

                if (!_running || _disposed)
                {
                    continue;
                }

                var count = Interlocked.Increment(ref _count);
                if (!IsDue(count))
                {
                    continue;
                }

                if (Interlocked.CompareExchange(ref _pending, 1, 0) == 0 && _window != 0)
                {
                    Interlocked.Exchange(ref _postedAt, Stopwatch.GetTimestamp());
                    User32.PostMessage(_window, TickMessage, 0, 0);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Error("The orb frame clock stopped.", ex);
        }
        finally
        {
            output?.Dispose();
        }
    }

    private static IDXGIOutput? PrimaryOutput()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        if (factory.EnumAdapters1(0, out var adapter).Failure || adapter is null)
        {
            return null;
        }

        using (adapter)
        {
            return adapter.EnumOutputs(0, out var output).Success ? output : null;
        }
    }

    private void OnTick()
    {
        _pending = 0;
        if (!_running)
        {
            return;
        }

        var device = GpuDevice.Shared;
        device?.BeginTick();
        if (!FrameStats.Enabled)
        {
            _tick(Count);
            device?.EndTick();
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var input = OrbNative.InputPending();
        FrameStats.BeginTick();
        _tick(Count);
        device?.EndTick();
        FrameStats.EndTick(start, Interlocked.Read(ref _postedAt), input);
    }

    private static void EnsureClass()
    {
        if (s_registered)
        {
            return;
        }

        fixed (char* name = ClassName)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)sizeof(WNDCLASSEX),
                lpfnWndProc = &WindowProc,
                hInstance = Kernel32.GetModuleHandle(null),
                lpszClassName = name,
            };
            User32.RegisterClassEx(wc);
        }

        s_registered = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        try
        {
            if (message == TickMessage)
            {
                s_instance?.OnTick();
                return 0;
            }

            if (message == OrbNative.WM_POWERBROADCAST && wParam == OrbNative.PBT_POWERSETTINGCHANGE && lParam != 0)
            {
                var setting = (OrbNative.PowerBroadcastSetting*)lParam;
                if (setting->PowerSetting == OrbNative.ConsoleDisplayState && setting->DataLength >= 4)
                {
                    var off = setting->Data == 0;
                    if (off != DisplayOff)
                    {
                        DisplayOff = off;
                        DisplayStateChanged?.Invoke(null, EventArgs.Empty);
                    }
                }

                return 1;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("An orb frame tick failed.", ex);
        }

        return User32.DefWindowProc(hwnd, message, wParam, lParam);
    }
}
