using AgentSpeed.Probe;

if (args is ["traffic-helper", ..])
{
    return TrafficHelperProbe.Run(args[1..]);
}

if (args is ["traffic", ..])
{
    return TrafficProbe.Run(args[1..]);
}

var options = ProbeOptions.Parse(args, out var error);
if (options is null)
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine(ProbeOptions.Usage);
    Console.Error.WriteLine(TrafficHelperProbe.Usage);
    Console.Error.WriteLine(TrafficProbe.Usage);
    return 2;
}

using var output = new ProbeOutput();
return options.Mode switch
{
    ProbeMode.DumpToday => DumpToday.Run(options, output),
    ProbeMode.DumpHistory => DumpHistory.Run(options, output),
    ProbeMode.DumpUnpriced => DumpUnpriced.Run(options, output),
    ProbeMode.SyncPrices => NetworkSync.Prices(options, output),
    ProbeMode.SyncRates => NetworkSync.Rates(options, output),
    _ => Watch.Run(options, output),
};
