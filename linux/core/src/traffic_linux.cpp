#include "agentspeed/traffic_linux.hpp"

#include <dirent.h>
#include <linux/inet_diag.h>
#include <linux/netlink.h>
#include <linux/rtnetlink.h>
#include <linux/sock_diag.h>
#include <linux/tcp.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <unistd.h>

#include <algorithm>
#include <cstring>
#include <fstream>
#include <sstream>

#include "agentspeed/fs_util.hpp"

namespace agentspeed::traffic::sampler {

namespace {

std::string readFile(const std::string& path) {
    std::ifstream in(path, std::ios::binary);
    std::stringstream buffer;
    buffer << in.rdbuf();
    return buffer.str();
}

std::string readLink(const std::string& path) {
    char buffer[4096];
    const auto n = ::readlink(path.c_str(), buffer, sizeof buffer - 1);
    if (n <= 0) return {};
    std::string out(buffer, static_cast<std::size_t>(n));
    if (out.ends_with(" (deleted)")) out.resize(out.size() - 10);
    return out;
}

bool isNumber(const char* name) {
    if (!*name) return false;
    for (const char* c = name; *c; ++c) {
        if (*c < '0' || *c > '9') return false;
    }
    return true;
}

bool loopback4(const __be32 address) { return (ntohl(address) >> 24) == 127; }

bool loopback6(const __be32 address[4]) {
    const bool v4mapped = address[0] == 0 && address[1] == 0 && address[2] == htonl(0x0000FFFF);
    if (v4mapped) return loopback4(address[3]);
    return address[0] == 0 && address[1] == 0 && address[2] == 0 && address[3] == htonl(1);
}

bool dumpFamily(int family, unsigned uid, std::map<std::uint64_t, std::pair<std::uint64_t, std::uint64_t>>& out) {
    const int fd = ::socket(AF_NETLINK, SOCK_RAW | SOCK_CLOEXEC, NETLINK_SOCK_DIAG);
    if (fd < 0) return false;
    struct {
        nlmsghdr header;
        inet_diag_req_v2 request;
    } message{};
    message.header.nlmsg_len = sizeof message;
    message.header.nlmsg_type = SOCK_DIAG_BY_FAMILY;
    message.header.nlmsg_flags = NLM_F_REQUEST | NLM_F_DUMP;
    message.request.sdiag_family = static_cast<__u8>(family);
    message.request.sdiag_protocol = IPPROTO_TCP;
    message.request.idiag_ext = 1 << (INET_DIAG_INFO - 1);
    // Every state but LISTEN: established sockets, and closing ones whose last bytes still count.
    message.request.idiag_states = ~(1U << 10);
    sockaddr_nl kernel{};
    kernel.nl_family = AF_NETLINK;
    if (::sendto(fd, &message, sizeof message, 0, reinterpret_cast<sockaddr*>(&kernel), sizeof kernel) < 0) {
        ::close(fd);
        return false;
    }
    alignas(nlmsghdr) char buffer[32768];
    bool done = false, ok = true;
    while (!done) {
        const auto length = ::recv(fd, buffer, sizeof buffer, 0);
        if (length <= 0) {
            ok = false;
            break;
        }
        int remaining = static_cast<int>(length);
        for (auto* h = reinterpret_cast<nlmsghdr*>(buffer); NLMSG_OK(h, remaining); h = NLMSG_NEXT(h, remaining)) {
            if (h->nlmsg_type == NLMSG_DONE) {
                done = true;
                break;
            }
            if (h->nlmsg_type == NLMSG_ERROR) {
                done = true;
                ok = false;
                break;
            }
            const auto* diag = static_cast<const inet_diag_msg*>(NLMSG_DATA(h));
            if (diag->idiag_uid != uid || diag->idiag_inode == 0) continue;
            const bool local = family == AF_INET ? loopback4(diag->id.idiag_dst[0]) || loopback4(diag->id.idiag_src[0])
                                                 : loopback6(diag->id.idiag_dst) || loopback6(diag->id.idiag_src);
            if (local) continue;
            int attributes = static_cast<int>(h->nlmsg_len - NLMSG_LENGTH(sizeof *diag));
            for (auto* a = reinterpret_cast<rtattr*>(const_cast<inet_diag_msg*>(diag) + 1); RTA_OK(a, attributes); a = RTA_NEXT(a, attributes)) {
                if (a->rta_type != INET_DIAG_INFO) continue;
                tcp_info info{};
                std::memcpy(&info, RTA_DATA(a), std::min<std::size_t>(sizeof info, RTA_PAYLOAD(a)));
                out[diag->idiag_inode] = {info.tcpi_bytes_received, info.tcpi_bytes_acked};
            }
        }
    }
    ::close(fd);
    return ok;
}

}  // namespace

Interfaces machineCounters() {
    Interfaces out;
    std::istringstream lines(readFile("/proc/net/dev"));
    std::string line;
    while (std::getline(lines, line)) {
        const auto colon = line.find(':');
        if (colon == std::string::npos) continue;
        std::string name = line.substr(0, colon);
        name.erase(0, name.find_first_not_of(' '));
        if (name.empty() || name == "lo") continue;
        // Physical interfaces only (Ethernet, Wi-Fi, USB tethering): virtual ones (bridges, veth, tun, wireguard,
        // docker) have no `device` link. Counted while up.
        if (!fsx::exists("/sys/class/net/" + name + "/device")) continue;
        auto state = readFile("/sys/class/net/" + name + "/operstate");
        while (!state.empty() && (state.back() == '\n' || state.back() == ' ')) state.pop_back();
        if (state != "up") continue;
        std::istringstream fields(line.substr(colon + 1));
        std::uint64_t value = 0, rx = 0, tx = 0;
        for (int i = 0; i < 9 && fields >> value; ++i) {
            if (i == 0) rx = value;
            if (i == 8) tx = value;
        }
        out[name] = {rx, tx};
    }
    return out;
}

ProcessReader::ProcessReader() : ticksPerSecond_(::sysconf(_SC_CLK_TCK)), uid_(::getuid()) {
    if (ticksPerSecond_ <= 0) ticksPerSecond_ = 100;
}

std::vector<Process> ProcessReader::read() {
    std::vector<Process> out;
    DIR* proc = ::opendir("/proc");
    if (!proc) return out;
    std::set<Key> alive;
    while (const dirent* entry = ::readdir(proc)) {
        if (!isNumber(entry->d_name)) continue;
        const std::string base = std::string("/proc/") + entry->d_name;
        struct stat st {};
        if (::stat(base.c_str(), &st) != 0 || st.st_uid != uid_) continue;
        const auto stat = readFile(base + "/stat");
        const auto close = stat.rfind(')');
        if (close == std::string::npos) continue;
        std::istringstream fields(stat.substr(close + 2));
        std::string state;
        std::int64_t ppid = 0;
        fields >> state >> ppid;
        std::string skip;
        for (int i = 0; i < 17; ++i) fields >> skip;  // fields 5 … 21
        std::uint64_t startTicks = 0;
        fields >> startTicks;
        Process p;
        p.pid = std::stoll(entry->d_name);
        p.ppid = ppid;
        p.start = static_cast<std::int64_t>(startTicks * 1000 / static_cast<std::uint64_t>(ticksPerSecond_));
        const Key key{p.pid, p.start};
        alive.insert(key);
        auto it = cache_.find(key);
        if (it == cache_.end()) {
            Cached c;
            c.exe = readLink(base + "/exe");
            const auto cmdline = readFile(base + "/cmdline");
            std::size_t at = 0;
            while (at < cmdline.size()) {
                const auto end = cmdline.find('\0', at);
                c.args.push_back(cmdline.substr(at, end == std::string::npos ? std::string::npos : end - at));
                if (end == std::string::npos) break;
                at = end + 1;
            }
            if (const auto cwd = readLink(base + "/cwd"); !cwd.empty()) c.cwd = cwd;
            it = cache_.emplace(key, std::move(c)).first;
        }
        p.exe = it->second.exe;
        p.args = it->second.args;
        p.cwd = it->second.cwd;
        out.push_back(std::move(p));
    }
    ::closedir(proc);
    std::erase_if(cache_, [&](const auto& entry) { return !alive.contains(entry.first); });
    return out;
}

void ProcessReader::refreshOpenFiles(std::vector<Process>& processes, const std::set<Key>& wanted) {
    for (auto& p : processes) {
        if (!wanted.contains({p.pid, p.start})) continue;
        p.openFiles.clear();
        const auto dir = "/proc/" + std::to_string(p.pid) + "/fd";
        DIR* fds = ::opendir(dir.c_str());
        if (!fds) continue;
        while (const dirent* entry = ::readdir(fds)) {
            if (!isNumber(entry->d_name)) continue;
            const auto target = readLink(dir + "/" + entry->d_name);
            if (!target.empty() && target[0] == '/') p.openFiles.push_back(target);
        }
        ::closedir(fds);
    }
}

std::map<std::uint64_t, std::pair<std::uint64_t, std::uint64_t>> tcpSockets(bool& ok) {
    std::map<std::uint64_t, std::pair<std::uint64_t, std::uint64_t>> out;
    const unsigned uid = ::getuid();
    ok = dumpFamily(AF_INET, uid, out);
    ok = dumpFamily(AF_INET6, uid, out) && ok;
    return out;
}

Counters SocketCounters::sample(const std::vector<Key>& processes) {
    Counters counters;
    counters.stream = stream_;
    bool ok = false;
    const auto sockets = tcpSockets(ok);
    available_ = ok;
    // Each socket counts once, for the latest-started process that holds it (a child using an inherited socket).
    std::vector<Key> order = processes;
    std::sort(order.begin(), order.end(), [](const Key& a, const Key& b) { return a.second != b.second ? a.second > b.second : a.first > b.first; });
    std::set<std::uint64_t> claimed;
    std::map<std::uint64_t, std::pair<std::uint64_t, std::uint64_t>> seen;
    for (const auto& key : order) {
        const auto dir = "/proc/" + std::to_string(key.first) + "/fd";
        DIR* fds = ::opendir(dir.c_str());
        if (!fds) continue;
        auto& total = totals_[key];
        while (const dirent* entry = ::readdir(fds)) {
            if (!isNumber(entry->d_name)) continue;
            const auto target = readLink(dir + "/" + entry->d_name);
            if (!target.starts_with("socket:[")) continue;
            const auto inode = std::stoull(target.substr(8));
            const auto it = sockets.find(inode);
            if (it == sockets.end() || !claimed.insert(inode).second) continue;
            const auto& [rx, tx] = it->second;
            seen[inode] = it->second;
            if (!started_) continue;  // a stream's first sample is its baseline
            const auto last = sockets_.find(inode);
            if (last == sockets_.end()) {
                total.first += rx;
                total.second += tx;
            } else {
                total.first += rx >= last->second.first ? rx - last->second.first : 0;
                total.second += tx >= last->second.second ? tx - last->second.second : 0;
            }
        }
        ::closedir(fds);
        counters.rows.push_back({key.first, key.second, total.first, total.second});
    }
    sockets_ = std::move(seen);
    started_ = true;
    const std::set<Key> wanted(processes.begin(), processes.end());
    std::erase_if(totals_, [&](const auto& entry) { return !wanted.contains(entry.first); });
    return counters;
}

}  // namespace agentspeed::traffic::sampler
