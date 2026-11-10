#pragma once

#include <cstddef>
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

// `agent-speed-statusline` (QUOTAS 5.5): Claude Code runs it as the status line command after each response. It merges
// the `rate_limits` of its input into the quota file, then runs the user's previous status line command with the same
// input, or prints the quota line. It never prints anything else, never logs the input and exits 0 on its own errors.
namespace agentspeed::quota::helper {

// Input beyond kInputLimit (QUOTAS 5.5 step 1) is not parsed, but still passed to the previous command up to this.
constexpr std::size_t kForwardLimit = 16u << 20;
constexpr int kCommandTimeoutMs = 10'000;

struct Options {
    // `<data>/quota`: the quota file and the sidecar.
    std::string quotaFolder;
    std::int64_t nowMs = 0;
    int commandTimeoutMs = kCommandTimeoutMs;
    std::string shell = "/bin/sh";
};

// `<folder above the executable's own folder>/quota` (the copy lives in `<data>/bin`), from the resolved executable path.
std::string quotaFolderBeside(std::string_view executable);
// The resolved `/proc/self/exe`.
std::optional<std::string> ownExecutable();

// Everything from `fd` up to `limit` bytes (EINTR retried; stops at EOF or an error).
std::string readInput(int fd, std::size_t limit = kForwardLimit);

// Steps 2 and 3 with the input already read: stores the quota, then runs the previous command (its exit code) or
// writes the quota line to `outFd` (0).
int run(std::string_view input, const Options& options, int outFd);

// Merges the input's `rate_limits` into `<quotaFolder>/claude-cli.json`, under an advisory lock on the folder, writing
// only when a value changed (temporary file, rename, 0600). True when the file was written.
bool store(std::string_view input, const std::string& quotaFolder, std::int64_t nowMs);

// `<shell> -c <command>` with `input` on its standard input and this process's environment, working folder, standard
// output and standard error; killed with everything it started after `timeoutMs`. Its exit code; 0 when it could not
// run, was killed or ended by a signal.
int runPrevious(const std::string& command, std::string_view input, int timeoutMs, const std::string& shell = "/bin/sh");

// The executable's whole run: `--folder <path>` (REMOTE 3.2: files in `<path>/quota/`), else beside the executable.
int main(int argc, char** argv);

}  // namespace agentspeed::quota::helper
