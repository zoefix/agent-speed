using System.Runtime.InteropServices;
using AgentSpeed.Core.Remote;
using AgentSpeed.Relay;

var version = typeof(RelaySession).Assembly.GetName().Version!.ToString(3);
if (args is ["--version"])
{
    Console.WriteLine($"agent-speed-relay {version} protocol {RemoteProtocol.Version}");
    return 0;
}

if (args is not ["serve"] || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
{
    Console.Error.WriteLine("Usage: agent-speed-relay serve");
    return 2;
}

try
{
    var session = new RelaySession(Console.OpenStandardOutput(), version);
    return session.Run(Console.OpenStandardInput());
}
catch (IOException)
{
    // The SSH channel can close while an update is being written.
    return 1;
}
