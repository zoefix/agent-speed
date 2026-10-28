namespace AgentSpeed.Linux.Platform;

/// <summary>Explicitly dispatches hub notifications even when created before the desktop run loop installs its context.</summary>
internal sealed class UiSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback callback, object? state) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => callback(state), Avalonia.Threading.DispatcherPriority.Normal);
}
