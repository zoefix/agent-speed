using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Core.Traffic.Monitor;
using AgentSpeed.Linux.Platform;

namespace AgentSpeed.Traffic;

internal sealed class TrafficService : IDisposable
{
    public TrafficHub Hub { get; } = new(new UiSynchronizationContext());
    public TrafficMonitor Monitor { get; }
    public TrafficService(SpeedHub speed)
    {
        Monitor = TrafficMonitor.CreateForLinux(Hub, LinuxPaths.History);
        speed.LivePublished += (_, snapshot) => Monitor.UpdateAgents(snapshot.Agents);
    }
    public void Dispose() => Monitor.Dispose();
}
