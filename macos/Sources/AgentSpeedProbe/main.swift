import AgentSpeedCore
import Foundation

guard let arguments = ProbeArguments.parse(Array(CommandLine.arguments.dropFirst())) else {
    FileHandle.standardError.write(Data((ProbeArguments.usage + "\n").utf8))
    exit(2)
}

switch arguments.mode {
case .dumpToday:
    DumpToday.run(configuration: arguments.configuration, table: arguments.priceTable(), now: arguments.now)
case .dumpHistory:
    DumpHistory.run(
        configuration: arguments.configuration, utc: arguments.utc, storePath: arguments.historyStore,
        table: arguments.priceTable()
    )
case .dumpUnpriced(let options):
    DumpUnpriced.run(
        configuration: arguments.configuration, options: options, storePath: arguments.historyStore,
        table: arguments.priceTable()
    )
case .watch(let seconds):
    WatchMode.run(configuration: arguments.configuration, seconds: seconds)
case .traffic(let seconds):
    TrafficMode.run(
        configuration: arguments.configuration, seconds: seconds, interval: arguments.trafficInterval,
        storePath: arguments.historyStore, live: !arguments.noLive)
case .syncPrices(let directory):
    print(await NetworkSync.prices(directory: URL(fileURLWithPath: directory, isDirectory: true)))
case .syncRates(let directory, let currency):
    print(await NetworkSync.rates(directory: URL(fileURLWithPath: directory, isDirectory: true), currency: currency))
}
