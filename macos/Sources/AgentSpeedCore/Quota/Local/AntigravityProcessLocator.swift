import Darwin
import Foundation

/// The macOS side of QUOTAS 9.4: the user's `language_server_*` and `agy` processes from libproc, the arguments of a
/// language server, the two `ANTIGRAVITY_*` variables of an `agy` process's own environment (`KERN_PROCARGS2`; no
/// other process's environment is ever read, and no other variable is kept), their listening TCP sockets on
/// loopback (`proc_pidfdinfo`), and the daemon files while their pid runs.
public final class AntigravityProcessLocator: AntigravityLocating, @unchecked Sendable {
    private let home: URL
    private let reader = ProcessReader()
    private let lock = NSLock()

    public init(home: URL = FileManager.default.homeDirectoryForCurrentUser) {
        self.home = home
    }

    public func servers() -> [AntigravityServer] {
        lock.lock()
        defer { lock.unlock() }
        var found: [AntigravityServer] = []
        var pids: Set<Int32> = []
        for entry in reader.list(nowMs: Clock.milliseconds(Date())) {
            pids.insert(entry.pid)
            guard let executable = reader.executablePath(of: entry) else { continue }
            let name = (executable as NSString).lastPathComponent
            if name.hasPrefix("language_server_") {
                let arguments = reader.arguments(of: entry) ?? []
                guard AntigravityRules.isLanguageServer(executable: executable, arguments: arguments) else { continue }
                found.append(AntigravityRules.languageServer(pid: entry.pid, arguments: arguments,
                                                             listening: Self.listeningPorts(entry.pid)))
            } else if name == "agy" {
                let environment = Self.environment(of: entry.pid, names: AntigravityRules.environmentNames)
                if let server = AntigravityRules.agy(pid: entry.pid, environment: environment,
                                                     listening: Self.listeningPorts(entry.pid)) {
                    found.append(server)
                }
            }
            if found.count >= 4 { break }
        }
        for file in AntigravityRules.daemonFiles(home: home) {
            guard let data = QuotaCredentials.file(file), let server = AntigravityRules.daemon(data),
                  pids.contains(server.pid), !found.contains(where: { $0.pid == server.pid }) else { continue }
            found.append(server)
        }
        return found
    }

    /// Only `names` of the process's environment; the other strings are passed over and never kept.
    static func environment(of pid: Int32, names: Set<String>) -> [String: String] {
        var mib: [Int32] = [CTL_KERN, KERN_PROCARGS2, pid]
        var size = 0
        guard sysctl(&mib, 3, nil, &size, nil, 0) == 0, size > 4, size <= 4 << 20 else { return [:] }
        var buffer = [UInt8](repeating: 0, count: size)
        defer { for index in buffer.indices { buffer[index] = 0 } }
        guard buffer.withUnsafeMutableBytes({ sysctl(&mib, 3, $0.baseAddress, &size, nil, 0) }) == 0 else { return [:] }
        return buffer.withUnsafeBytes { parseEnvironment(UnsafeRawBufferPointer(rebasing: $0[0..<size]), names: names) }
    }

    /// `KERN_PROCARGS2`: argc, the executable path, padding, argv, then the environment's `NAME=value` strings.
    static func parseEnvironment(_ bytes: UnsafeRawBufferPointer, names: Set<String>) -> [String: String] {
        guard bytes.count >= 4 else { return [:] }
        let argc = Int(bytes.loadUnaligned(as: Int32.self))
        var index = 4
        while index < bytes.count, bytes[index] != 0 { index += 1 }
        while index < bytes.count, bytes[index] == 0 { index += 1 }
        var skipped = 0
        while skipped < argc, index < bytes.count {
            while index < bytes.count, bytes[index] != 0 { index += 1 }
            index += 1
            skipped += 1
        }
        var found: [String: String] = [:]
        while index < bytes.count, bytes[index] != 0 {
            let start = index
            while index < bytes.count, bytes[index] != 0 { index += 1 }
            let entry = UnsafeRawBufferPointer(rebasing: bytes[start..<index])
            if let equals = entry.firstIndex(of: UInt8(ascii: "=")) {
                let name = String(decoding: UnsafeRawBufferPointer(rebasing: entry[..<equals]), as: UTF8.self)
                if names.contains(name) {
                    found[name] = String(decoding: UnsafeRawBufferPointer(rebasing: entry[(equals + 1)...]), as: UTF8.self)
                }
            }
            index += 1
        }
        return found
    }

    /// The process's TCP sockets listening on 127.0.0.1 or on every address (IPv4, or dual-stack IPv6), in descriptor order.
    static func listeningPorts(_ pid: Int32) -> [Int] {
        let needed = proc_pidinfo(pid, PROC_PIDLISTFDS, 0, nil, 0)
        guard needed > 0 else { return [] }
        let stride = MemoryLayout<proc_fdinfo>.stride
        var fds = [proc_fdinfo](repeating: proc_fdinfo(), count: Int(needed) / stride + 16)
        let bytes = fds.withUnsafeMutableBytes { proc_pidinfo(pid, PROC_PIDLISTFDS, 0, $0.baseAddress, Int32($0.count)) }
        guard bytes > 0 else { return [] }
        var ports: [Int] = []
        var info = socket_fdinfo()
        let size = Int32(MemoryLayout<socket_fdinfo>.size)
        for fd in fds.prefix(Int(bytes) / stride) where fd.proc_fdtype == UInt32(PROX_FDTYPE_SOCKET) {
            guard proc_pidfdinfo(pid, fd.proc_fd, PROC_PIDFDSOCKETINFO, &info, size) == size,
                  info.psi.soi_kind == Int32(SOCKINFO_TCP) else { continue }
            let tcp = info.psi.soi_proto.pri_tcp
            guard tcp.tcpsi_state == Int32(TSI_S_LISTEN) else { continue }
            let ini = tcp.tcpsi_ini
            let port = Int(UInt16(bigEndian: UInt16(truncatingIfNeeded: ini.insi_lport)))
            if ini.insi_vflag & UInt8(INI_IPV4) != 0 {
                let address = UInt32(bigEndian: ini.insi_laddr.ina_46.i46a_addr4.s_addr)
                guard address == 0x7F00_0001 || address == 0 else { continue }
            } else if ini.insi_vflag & UInt8(INI_IPV6) != 0 {
                let words = ini.insi_laddr.ina_6.__u6_addr.__u6_addr32
                guard words.0 == 0, words.1 == 0, words.2 == 0, words.3 == 0 else { continue }
            } else {
                continue
            }
            if port > 0, !ports.contains(port) { ports.append(port) }
            if ports.count >= 4 { break }
        }
        return ports
    }
}
