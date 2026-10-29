#pragma once

#include <cstdint>
#include <map>
#include <set>
#include <string>
#include <vector>

#include "agentspeed/traffic.hpp"

// The Linux samplers of METRICS 10 (the spec names macOS and Windows ones; these follow the same contract):
// - machine: /proc/net/dev for the physical interfaces (those with a `device` link in /sys/class/net) that are up;
// - processes: the current user's /proc entries (pid, parent, start time, executable, arguments, working directory);
// - process counters: TCP byte counters of the user's non-loopback sockets from netlink sock_diag (tcp_info
//   bytes_received / bytes_acked), summed per process through /proc/<pid>/fd. UDP (QUIC) has no counters there and is
//   not counted (a known approximation, like 10.12's).
namespace agentspeed::traffic::sampler {

Interfaces machineCounters();

class ProcessReader {
public:
    ProcessReader();
    // Every process of the current user; arguments and paths are read once per identity.
    std::vector<Process> read();
    // Refreshes the open files of the given processes (agent processes, for 10.6 evidence).
    void refreshOpenFiles(std::vector<Process>& processes, const std::set<Key>& wanted);

private:
    struct Cached {
        std::string exe;
        std::vector<std::string> args;
        std::optional<std::string> cwd;
    };
    std::map<Key, Cached> cache_;
    long ticksPerSecond_;
    unsigned uid_;
};

// Cumulative per-process TCP counters built from per-socket deltas, so a closing socket loses nothing counted.
class SocketCounters {
public:
    explicit SocketCounters(std::string stream) : stream_(std::move(stream)) {}
    // Rows for the given processes (those the table attributes); `first` marks a stream's first sample.
    Counters sample(const std::vector<Key>& processes);
    bool available() const { return available_; }

private:
    std::string stream_;
    bool started_ = false;
    bool available_ = true;
    std::map<std::uint64_t, std::pair<std::uint64_t, std::uint64_t>> sockets_;  // inode → last (rx, tx)
    std::map<Key, std::pair<std::uint64_t, std::uint64_t>> totals_;
};

// inode → (bytes received, bytes acked) of the current user's TCP sockets with a non-loopback peer.
std::map<std::uint64_t, std::pair<std::uint64_t, std::uint64_t>> tcpSockets(bool& ok);

}  // namespace agentspeed::traffic::sampler
