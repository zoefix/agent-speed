#include "agentspeed/quota/routes/antigravity.hpp"

#include <algorithm>
#include <cctype>
#include <charconv>
#include <set>

#include "agentspeed/json.hpp"
#include "agentspeed/quota/routes/fingerprint.hpp"

namespace agentspeed::quota {

namespace {

constexpr std::size_t kMaxEndpoints = 8;
constexpr std::size_t kMaxServers = 4;
constexpr std::size_t kMaxPorts = 4;
constexpr std::size_t kCmdlineBytes = 256 << 10;
constexpr std::size_t kEnvironBytes = 1 << 20;
constexpr std::size_t kTableBytes = 16 << 20;
constexpr std::size_t kDaemonBytes = 64 << 10;
constexpr std::size_t kDaemonFiles = 16;
constexpr std::string_view kTokenVariable = "ANTIGRAVITY_CSRF_TOKEN";
constexpr std::string_view kAddressVariable = "ANTIGRAVITY_LS_ADDRESS";

std::string lower(std::string_view text) {
    std::string out(text);
    for (char& c : out) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return out;
}

std::string_view baseName(std::string_view path) {
    const auto slash = path.rfind('/');
    return slash == std::string_view::npos ? path : path.substr(slash + 1);
}

template <typename T>
std::optional<T> integer(std::string_view text, int base = 10) {
    T value{};
    if (text.empty()) return std::nullopt;
    const auto [end, ec] = std::from_chars(text.data(), text.data() + text.size(), value, base);
    if (ec != std::errc() || end != text.data() + text.size()) return std::nullopt;
    return value;
}

std::vector<std::string_view> fields(std::string_view line) {
    std::vector<std::string_view> out;
    std::size_t i = 0;
    while (i < line.size()) {
        while (i < line.size() && (line[i] == ' ' || line[i] == '\t')) ++i;
        const std::size_t start = i;
        while (i < line.size() && line[i] != ' ' && line[i] != '\t') ++i;
        if (i > start) out.push_back(line.substr(start, i - start));
    }
    return out;
}

// 127.0.0.1 or every address, as /proc/net/tcp{,6} print them (32-bit words in host order, either endianness).
bool loopbackOrAny(std::string_view address) {
    static const std::set<std::string_view> kAccepted = {
        "0100007F", "7F000001", "00000000",
        "00000000000000000000000000000000",
        "0000000000000000FFFF00000100007F",
        "00000000000000000000FFFF7F000001",
    };
    return kAccepted.count(address) > 0;
}

std::vector<std::string> nulSeparated(std::string_view block) {
    std::vector<std::string> out;
    std::size_t start = 0;
    while (start < block.size()) {
        auto end = block.find('\0', start);
        if (end == std::string_view::npos) end = block.size();
        out.emplace_back(block.substr(start, end - start));
        start = end + 1;
    }
    return out;
}

}  // namespace

AntigravityServer antigravityServer(std::int64_t pid, const std::vector<AntigravityEndpoint>& endpoints) {
    AntigravityServer server{pid, {}};
    for (const auto& endpoint : endpoints) {
        if (endpoint.port < 1 || endpoint.port > 65535) continue;
        const bool seen = std::any_of(server.endpoints.begin(), server.endpoints.end(), [&](const AntigravityEndpoint& e) {
            return e.scheme == endpoint.scheme && e.port == endpoint.port;
        });
        if (seen) continue;
        server.endpoints.push_back(endpoint);
        // A token that could break the header is left out, like a missing one.
        auto& token = server.endpoints.back().token;
        if (token && !headerSafe(*token)) token.reset();
        if (server.endpoints.size() >= kMaxEndpoints) break;
    }
    return server;
}

namespace antigravity {

bool isLanguageServer(std::string_view executable, const std::vector<std::string>& arguments) {
    if (baseName(executable).substr(0, 16) != "language_server_") return false;
    const bool flagged = std::any_of(arguments.begin(), arguments.end(), [](const std::string& a) {
        return a == "--app_data_dir" || a.rfind("--app_data_dir=", 0) == 0;
    });
    return flagged && std::any_of(arguments.begin(), arguments.end(),
                                  [](const std::string& a) { return lower(a).find("antigravity") != std::string::npos; });
}

std::optional<std::string> flag(std::string_view name, const std::vector<std::string>& arguments) {
    const std::string prefix = std::string(name) + "=";
    for (std::size_t i = 0; i < arguments.size(); ++i) {
        if (arguments[i] == name && i + 1 < arguments.size()) return arguments[i + 1];
        if (arguments[i].rfind(prefix, 0) == 0) return arguments[i].substr(prefix.size());
    }
    return std::nullopt;
}

AntigravityServer languageServer(std::int64_t pid, const std::vector<std::string>& arguments, const std::vector<int>& listening) {
    const auto csrf = flag("--csrf_token", arguments);
    std::vector<AntigravityEndpoint> endpoints;
    if (const auto port = flag("--extension_server_port", arguments)) {
        if (const auto number = integer<int>(*port)) {
            auto token = flag("--extension_server_csrf_token", arguments);
            endpoints.push_back({"http", *number, token ? token : csrf});
        }
    }
    for (const int port : listening) {
        endpoints.push_back({"http", port, csrf});
        endpoints.push_back({"https", port, csrf});
    }
    return antigravityServer(pid, endpoints);
}

std::optional<AntigravityServer> agy(std::int64_t pid, const std::optional<std::string>& token,
                                     const std::optional<std::string>& address, const std::vector<int>& listening) {
    std::vector<int> ports;
    if (address) {
        const auto colon = address->rfind(':');
        if (colon != std::string::npos) {
            std::string host = address->substr(0, colon);
            host.erase(std::remove(host.begin(), host.end(), '['), host.end());
            host.erase(std::remove(host.begin(), host.end(), ']'), host.end());
            const auto port = integer<int>(std::string_view(*address).substr(colon + 1));
            if ((host == "127.0.0.1" || host == "localhost") && port) ports.push_back(*port);
        }
    }
    for (const int port : listening) ports.push_back(port);
    if (ports.empty()) return std::nullopt;
    std::vector<AntigravityEndpoint> endpoints;
    for (const int port : ports) {
        endpoints.push_back({"http", port, token});
        endpoints.push_back({"https", port, token});
    }
    return antigravityServer(pid, endpoints);
}

std::optional<AntigravityServer> daemon(std::string_view text) {
    if (text.size() > kDaemonBytes) return std::nullopt;
    json::Document doc;
    if (!doc.parse(text) || !doc.root().isObject()) return std::nullopt;
    const auto root = doc.root();
    const auto pid = root["pid"].isIntegerLiteral() ? root["pid"].truncated() : std::nullopt;
    if (!pid || *pid <= 0 || *pid > 0x7FFFFFFF) return std::nullopt;
    const auto token = root["csrfToken"].string();
    std::vector<AntigravityEndpoint> endpoints;
    const auto port = [&](std::string_view key) -> std::optional<int> {
        const auto value = root[key];
        if (!value.isIntegerLiteral()) return std::nullopt;
        const auto n = value.truncated();
        if (!n || *n < 1 || *n > 65535) return std::nullopt;
        return static_cast<int>(*n);
    };
    if (const auto plain = port("httpPort")) endpoints.push_back({"http", *plain, token});
    if (const auto secure = port("httpsPort")) endpoints.push_back({"https", *secure, token});
    if (endpoints.empty()) return std::nullopt;
    return antigravityServer(*pid, endpoints);
}

std::map<std::uint64_t, int> listeningSockets(std::string_view table) {
    std::map<std::uint64_t, int> out;
    std::size_t start = 0;
    bool header = true;
    while (start < table.size()) {
        auto end = table.find('\n', start);
        if (end == std::string_view::npos) end = table.size();
        const auto line = table.substr(start, end - start);
        start = end + 1;
        if (header) {
            header = false;
            continue;
        }
        const auto f = fields(line);
        if (f.size() < 10 || f[3] != "0A") continue;
        const auto colon = f[1].rfind(':');
        if (colon == std::string_view::npos || !loopbackOrAny(f[1].substr(0, colon))) continue;
        const auto port = integer<int>(f[1].substr(colon + 1), 16);
        const auto inode = integer<std::uint64_t>(f[9]);
        if (!port || *port < 1 || *port > 65535 || !inode || *inode == 0) continue;
        out.emplace(*inode, *port);
    }
    return out;
}

std::map<std::string, std::string> environmentValues(std::string_view block, const std::vector<std::string_view>& names) {
    std::map<std::string, std::string> found;
    std::size_t start = 0;
    while (start < block.size()) {
        auto end = block.find('\0', start);
        if (end == std::string_view::npos) end = block.size();
        const auto entry = block.substr(start, end - start);
        start = end + 1;
        const auto equals = entry.find('=');
        if (equals == std::string_view::npos) continue;
        const auto name = entry.substr(0, equals);
        if (std::find(names.begin(), names.end(), name) != names.end()) found[std::string(name)] = std::string(entry.substr(equals + 1));
    }
    return found;
}

}  // namespace antigravity

std::vector<int> ProcAntigravityLocator::listeningPorts(std::int64_t pid, std::optional<std::map<std::uint64_t, int>>& sockets) {
    auto& files = env_.fs();
    if (!sockets) {
        sockets.emplace();
        for (const auto* table : {"net/tcp", "net/tcp6"}) {
            if (const auto text = files.read(fsx::join(env_.procRoot, table), kTableBytes)) sockets->merge(antigravity::listeningSockets(*text));
        }
    }
    const auto fdFolder = fsx::join(fsx::join(env_.procRoot, std::to_string(pid)), "fd");
    auto descriptors = files.list(fdFolder);
    std::sort(descriptors.begin(), descriptors.end(), [](const std::string& a, const std::string& b) {
        return integer<int>(a).value_or(0) < integer<int>(b).value_or(0);
    });
    std::vector<int> ports;
    for (const auto& fd : descriptors) {
        const auto target = files.readLink(fsx::join(fdFolder, fd));
        if (!target || target->rfind("socket:[", 0) != 0 || target->back() != ']') continue;
        const auto inode = integer<std::uint64_t>(std::string_view(*target).substr(8, target->size() - 9));
        if (!inode) continue;
        const auto found = sockets->find(*inode);
        if (found == sockets->end() || std::find(ports.begin(), ports.end(), found->second) != ports.end()) continue;
        ports.push_back(found->second);
        if (ports.size() >= kMaxPorts) break;
    }
    return ports;
}

std::vector<AntigravityServer> ProcAntigravityLocator::servers() {
    auto& files = env_.fs();
    std::vector<std::int64_t> pids;
    for (const auto& name : files.list(env_.procRoot)) {
        if (const auto pid = integer<std::int64_t>(name); pid && *pid > 0) pids.push_back(*pid);
    }
    std::sort(pids.begin(), pids.end());
    std::vector<AntigravityServer> found;
    std::set<std::int64_t> own;
    std::optional<std::map<std::uint64_t, int>> sockets;
    for (const auto pid : pids) {
        const auto folder = fsx::join(env_.procRoot, std::to_string(pid));
        auto exe = files.readLink(fsx::join(folder, "exe"));
        if (!exe) continue;
        own.insert(pid);
        constexpr std::string_view kDeleted = " (deleted)";
        if (exe->size() > kDeleted.size() && exe->compare(exe->size() - kDeleted.size(), kDeleted.size(), kDeleted) == 0) {
            exe->resize(exe->size() - kDeleted.size());
        }
        if (found.size() >= kMaxServers) continue;
        const auto name = baseName(*exe);
        if (name.substr(0, 16) == "language_server_") {
            const auto cmdline = files.read(fsx::join(folder, "cmdline"), kCmdlineBytes);
            if (!cmdline) continue;
            auto arguments = nulSeparated(*cmdline);
            if (!arguments.empty()) arguments.erase(arguments.begin());
            if (!antigravity::isLanguageServer(*exe, arguments)) continue;
            found.push_back(antigravity::languageServer(pid, arguments, listeningPorts(pid, sockets)));
            for (auto& argument : arguments) wipe(argument);
        } else if (name == "agy") {
            auto block = files.read(fsx::join(folder, "environ"), kEnvironBytes);
            if (!block) continue;
            auto values = antigravity::environmentValues(*block, {kTokenVariable, kAddressVariable});
            wipe(*block);
            std::optional<std::string> token, address;
            if (auto it = values.find(std::string(kTokenVariable)); it != values.end() && !it->second.empty()) token = it->second;
            if (auto it = values.find(std::string(kAddressVariable)); it != values.end()) address = it->second;
            for (auto& entry : values) wipe(entry.second);
            if (auto server = antigravity::agy(pid, token, address, listeningPorts(pid, sockets))) found.push_back(std::move(*server));
            if (token) wipe(*token);
        }
    }
    for (const auto* product : {".gemini/antigravity/daemon", ".gemini/antigravity-ide/daemon"}) {
        const auto folder = env_.inHome(product);
        auto names = files.list(folder);
        std::sort(names.begin(), names.end());
        std::size_t read = 0;
        for (const auto& name : names) {
            if (name.rfind("ls_", 0) != 0 || name.size() < 8 || name.compare(name.size() - 5, 5, ".json") != 0) continue;
            if (++read > kDaemonFiles) break;
            auto text = files.read(fsx::join(folder, name), kDaemonBytes);
            if (!text) continue;
            auto server = antigravity::daemon(*text);
            wipe(*text);
            if (!server || !own.count(server->pid)) continue;
            const bool known = std::any_of(found.begin(), found.end(), [&](const AntigravityServer& s) { return s.pid == server->pid; });
            if (!known) found.push_back(std::move(*server));
        }
    }
    return found;
}

}  // namespace agentspeed::quota
