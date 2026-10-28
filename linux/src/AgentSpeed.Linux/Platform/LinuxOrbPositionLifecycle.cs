namespace AgentSpeed.Linux.Platform;

/// <summary>Orders position persistence before lifecycle operations that may suspend or close orb windows.</summary>
internal static class LinuxOrbPositionLifecycle
{
    internal static void Close(bool retainPosition, Action flushPosition, Action close, Action? removeFromLiveWindows = null)
    {
        removeFromLiveWindows?.Invoke();
        if (retainPosition) flushPosition();
        close();
    }

    internal static void Suspend(Action flushPositions, Action flushTraffic, Action flushSettings)
    {
        flushPositions();
        flushTraffic();
        flushSettings();
    }
}
