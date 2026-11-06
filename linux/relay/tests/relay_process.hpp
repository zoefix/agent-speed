#pragma once

#include <sys/types.h>

#include <chrono>
#include <optional>
#include <string>
#include <utility>
#include <vector>

// The relay executable as the app runs it: a child process with its own environment, spoken to over pipes.
namespace relay_test {

class RelayProcess {
public:
    RelayProcess(const std::string& binary, const std::vector<std::string>& args, const std::vector<std::string>& environment);
    ~RelayProcess();
    RelayProcess(const RelayProcess&) = delete;
    RelayProcess& operator=(const RelayProcess&) = delete;

    bool send(const std::string& line);
    // The next line, without its newline; nullopt at end of output or after the timeout.
    std::optional<std::string> readLine(std::chrono::milliseconds timeout);
    void closeInput();
    // The exit status, or nullopt when the process is still running after the timeout.
    std::optional<int> wait(std::chrono::milliseconds timeout);

private:
    pid_t pid_ = -1;
    int in_ = -1, out_ = -1;
    std::string buffer_;
    std::optional<int> status_;
};

// Runs the executable to its end and returns (exit status, stdout).
std::pair<int, std::string> runToEnd(const std::string& binary, const std::vector<std::string>& args);

}  // namespace relay_test
