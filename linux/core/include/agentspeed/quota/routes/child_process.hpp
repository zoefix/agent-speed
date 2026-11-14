#pragma once

#include <cstddef>
#include <cstdint>
#include <functional>
#include <string>
#include <vector>

// QUOTAS 9.4 Run: one program started in a process group of its own, given nothing of the app's but the environment it
// is handed: standard input from the null device, standard error discarded, standard output read up to a cap, no
// inherited descriptor. Its whole group is killed once it ends or is killed, so that no descendant stays.
namespace agentspeed::quota::child {

struct Command {
    std::string executable;  // an absolute path; never searched for
    std::vector<std::string> arguments;  // after argv[0], which is `executable`
    std::vector<std::string> environment;  // `NAME=value`
    std::string folder;  // the working folder
    std::int64_t timeoutMs = 0;
    std::size_t outputCap = 0;  // more is `TooLarge`
};

enum class End : std::uint8_t { NotStarted, Exited, Signaled, TimedOut, TooLarge, Stopped };

struct Result {
    End end = End::NotStarted;
    int code = -1;  // Exited
    std::string output;
    std::int64_t endedMs = 0;  // wall clock when it ended or was killed
};

// Blocks until the program ends. `stop`, asked every few tens of milliseconds, kills it when it says so.
Result run(const Command& command, const std::function<bool()>& stop = {});

}  // namespace agentspeed::quota::child
