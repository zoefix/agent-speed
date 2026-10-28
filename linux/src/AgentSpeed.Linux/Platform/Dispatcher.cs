namespace AgentSpeed.Linux.Platform;

// The shared application controllers only need posting and UI timers, never WPF objects.
internal enum DispatcherPriority { Normal, Background }

internal sealed class Dispatcher
{
    internal Avalonia.Threading.Dispatcher NativeDispatcher { get; } = Avalonia.Threading.Dispatcher.UIThread;
    public void BeginInvoke(Action action) => NativeDispatcher.Post(action);
}

internal sealed class DispatcherTimer
{
    private readonly Avalonia.Threading.DispatcherTimer _timer;

    internal Avalonia.Threading.Dispatcher NativeDispatcher => _timer.Dispatcher;
    internal bool IsEnabled => _timer.IsEnabled;

    public DispatcherTimer(DispatcherPriority priority, Dispatcher dispatcher) =>
        _timer = new Avalonia.Threading.DispatcherTimer(MapPriority(priority), dispatcher.NativeDispatcher);

    public DispatcherTimer(TimeSpan interval, DispatcherPriority priority, EventHandler tick, Dispatcher dispatcher)
    {
        _timer = new Avalonia.Threading.DispatcherTimer(interval, MapPriority(priority), dispatcher.NativeDispatcher, tick);
    }

    internal static Avalonia.Threading.DispatcherPriority MapPriority(DispatcherPriority priority) => priority switch
    {
        DispatcherPriority.Normal => Avalonia.Threading.DispatcherPriority.Normal,
        DispatcherPriority.Background => Avalonia.Threading.DispatcherPriority.Background,
        _ => throw new ArgumentOutOfRangeException(nameof(priority)),
    };

    public TimeSpan Interval { get => _timer.Interval; set => _timer.Interval = value; }
    public event EventHandler? Tick { add => _timer.Tick += value; remove => _timer.Tick -= value; }
    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
}
