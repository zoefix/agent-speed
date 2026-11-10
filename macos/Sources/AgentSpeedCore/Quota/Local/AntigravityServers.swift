import Foundation

/// A running Antigravity language server and the addresses to ask it on (QUOTAS 9.4), in order.
public struct AntigravityServer: Equatable, Sendable {
    public struct Endpoint: Equatable, Sendable {
        public var scheme: String
        public var port: Int
        public var token: String?
    }

    public var pid: Int32
    public var endpoints: [Endpoint]

    /// Dropped duplicates keep the first; at most 8 addresses per server.
    init(pid: Int32, endpoints: [Endpoint]) {
        self.pid = pid
        var seen: Set<String> = []
        self.endpoints = Array(endpoints.filter { (1...65535).contains($0.port) && seen.insert("\($0.scheme):\($0.port)").inserted }
            .prefix(8))
    }
}

/// Finds the running servers; never starts Antigravity.
public protocol AntigravityLocating: Sendable {
    func servers() -> [AntigravityServer]
}

/// The rules of QUOTAS 9.4 *Servers* and *Ports*, as pure functions over what a process lister found.
enum AntigravityRules {
    static let appBundles = ["/Antigravity.app/", "/Antigravity IDE.app/", "/Gemini.app/"]
    static let environmentNames: Set<String> = ["ANTIGRAVITY_CSRF_TOKEN", "ANTIGRAVITY_LS_ADDRESS"]

    /// A `language_server_*` executable of Antigravity: its arguments name `--app_data_dir` and antigravity, or it
    /// lies inside one of the apps.
    static func isLanguageServer(executable: String, arguments: [String]) -> Bool {
        let name = (executable as NSString).lastPathComponent
        guard name.hasPrefix("language_server_") else { return false }
        if appBundles.contains(where: executable.contains) { return true }
        let flagged = arguments.contains { $0 == "--app_data_dir" || $0.hasPrefix("--app_data_dir=") }
        return flagged && arguments.contains { $0.lowercased().contains("antigravity") }
    }

    /// `--name value` or `--name=value`.
    static func flag(_ name: String, in arguments: [String]) -> String? {
        for (index, argument) in arguments.enumerated() {
            if argument == name, index + 1 < arguments.count { return arguments[index + 1] }
            if argument.hasPrefix(name + "=") { return String(argument.dropFirst(name.count + 1)) }
        }
        return nil
    }

    /// The extension server's port first (its own token, else `--csrf_token`), then each listening loopback port over
    /// HTTP and then HTTPS with `--csrf_token`.
    static func languageServer(pid: Int32, arguments: [String], listening: [Int]) -> AntigravityServer {
        let csrf = flag("--csrf_token", in: arguments)
        var endpoints: [AntigravityServer.Endpoint] = []
        if let port = flag("--extension_server_port", in: arguments).flatMap({ Int($0) }) {
            let token = flag("--extension_server_csrf_token", in: arguments) ?? csrf
            endpoints.append(.init(scheme: "http", port: port, token: token))
        }
        for port in listening {
            endpoints.append(.init(scheme: "http", port: port, token: csrf))
            endpoints.append(.init(scheme: "https", port: port, token: csrf))
        }
        return AntigravityServer(pid: pid, endpoints: endpoints)
    }

    /// `agy`: `ANTIGRAVITY_LS_ADDRESS` (`host:port` on loopback) with `ANTIGRAVITY_CSRF_TOKEN`, then its listening ports.
    static func agy(pid: Int32, environment: [String: String], listening: [Int]) -> AntigravityServer? {
        let token = environment["ANTIGRAVITY_CSRF_TOKEN"]
        var ports: [Int] = []
        if let address = environment["ANTIGRAVITY_LS_ADDRESS"], let colon = address.lastIndex(of: ":") {
            let host = address[..<colon].trimmingCharacters(in: CharacterSet(charactersIn: "[]"))
            if ["127.0.0.1", "localhost"].contains(host), let port = Int(address[address.index(after: colon)...]) {
                ports.append(port)
            }
        }
        ports += listening
        guard !ports.isEmpty else { return nil }
        return AntigravityServer(pid: pid, endpoints: ports.flatMap { port in
            [AntigravityServer.Endpoint(scheme: "http", port: port, token: token), .init(scheme: "https", port: port, token: token)]
        })
    }

    /// A daemon file `ls_*.json`: `{pid, csrfToken, httpsPort, httpPort}`.
    static func daemon(_ data: Data) -> AntigravityServer? {
        guard data.count <= 64 << 10, let object = (try? JSONParser.parse(data))?.object,
              let pid = object["pid"]?.int64, pid > 0, pid <= Int64(Int32.max) else { return nil }
        let token = object["csrfToken"]?.string
        var endpoints: [AntigravityServer.Endpoint] = []
        if let port = object["httpPort"]?.int64 { endpoints.append(.init(scheme: "http", port: Int(port), token: token)) }
        if let port = object["httpsPort"]?.int64 { endpoints.append(.init(scheme: "https", port: Int(port), token: token)) }
        return endpoints.isEmpty ? nil : AntigravityServer(pid: Int32(pid), endpoints: endpoints)
    }

    /// The daemon folders' `ls_*.json` files.
    static func daemonFiles(home: URL) -> [URL] {
        ["antigravity", "antigravity-ide"].flatMap { name -> [URL] in
            let folder = home.appendingPathComponent(".gemini/\(name)/daemon")
            let names = (try? FileManager.default.contentsOfDirectory(atPath: folder.path)) ?? []
            return names.filter { $0.hasPrefix("ls_") && $0.hasSuffix(".json") }.sorted().prefix(16)
                .map { folder.appendingPathComponent($0) }
        }
    }
}
