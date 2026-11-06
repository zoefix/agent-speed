#pragma once

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

// The system OpenSSH client and the remote commands (REMOTE 4.1–4.3). Nothing here ever goes through a local shell.
namespace agentspeed::remote {

namespace ssh {
inline constexpr std::string_view kExecutable = "/usr/bin/ssh";
// Debug builds only: an absolute path in this variable adds `-F <path>` to every invocation (REMOTE 4.1).
inline constexpr std::string_view kConfigFileVariable = "AGENT_SPEED_SSH_CONFIG";

const std::vector<std::string>& options();
// The arguments after argv[0]; nullopt when the destination is not valid (REMOTE 2.1).
std::optional<std::vector<std::string>> arguments(std::string_view destination, std::string_view remoteCommand,
                                                  const std::optional<std::string>& configFile = std::nullopt);
// The file the development variable names in `environment` (`NAME=value` entries); always nullopt in release builds.
std::optional<std::string> developmentConfigFile(const std::vector<std::string>& environment);
// The environment plus `LC_ALL=C` (any other LC_ALL is replaced).
std::vector<std::string> environment(std::vector<std::string> base);
// This process's environment as `NAME=value` entries.
std::vector<std::string> processEnvironment();
// `ssh <destination>` for the user to run in a terminal (the first connection's host key check, DESIGN 13): the
// destination single-quoted unless it is made only of characters no POSIX shell treats specially.
std::string terminalCommand(std::string_view destination);
}  // namespace ssh

enum class Arch : std::uint8_t { X86_64, Aarch64 };
std::string_view archName(Arch arch);
// `uname -m` → architecture: x86_64 / amd64 and aarch64 / arm64 (any case).
std::optional<Arch> archOfMachine(std::string_view machine);

namespace relay_name {
inline constexpr std::string_view kInstalledPrefix = "agent-speed-relay-";
// `agent-speed-relay-linux-<arch>` in the app's relay folder.
std::string bundled(Arch arch);
// `agent-speed-relay-<version>-<arch>-<hash12>`; version bytes outside [A-Za-z0-9._-] become `-`.
std::string installed(std::string_view version, Arch arch, std::string_view sha256);
bool isSafe(std::string_view name);
}  // namespace relay_name

// The POSIX sh scripts (identical to the macOS app's): one line without `'`, `\`, `!` or a newline each.
namespace scripts {
extern const std::string kSelectHashTool;
extern const std::string kProbe;
extern const std::string kUpload;
extern const std::string kRun;
bool quotable(std::string_view script);
}  // namespace scripts

// `sh -c '<script>' agent-speed <arguments>`: the validated folder (unquoted, so `~` expands), a safe file name and a hex
// digest. Every builder returns nullopt for a value that fails validation.
namespace command {
inline constexpr int kMismatchStatus = 3;
inline constexpr int kWriteFailedStatus = 4;
inline constexpr int kNoHashToolStatus = 5;
std::optional<std::string> probe(std::string_view folder);
std::optional<std::string> upload(std::string_view folder, std::string_view fileName, std::string_view sha256);
std::optional<std::string> run(std::string_view folder, std::string_view fileName);
bool isDigest(std::string_view text);
}  // namespace command

// The probe command's output (REMOTE 4.2 step 1).
struct ProbeResult {
    static constexpr std::size_t kMaxOutputBytes = 64 * 1024;

    std::string system, kernel, machine;
    std::optional<std::string> hashTool;
    // Lowercase SHA-256 of each existing relay file by its (safe) name.
    std::map<std::string, std::string> hashes;
    bool complete = false;

    enum class Verdict : std::uint8_t { Supported, UnsupportedSystem, CannotVerify, Malformed };
    Verdict verdict() const;
    std::optional<Arch> arch() const { return archOfMachine(machine); }
    bool hasInstalled(const std::string& name, std::string_view sha256) const;

    // Lines it does not know are ignored (a login banner).
    static ProbeResult parse(std::string_view output);
    // The `hash <hex>` line of the upload script, lowercase.
    static std::optional<std::string> uploadedHash(std::string_view output);
};

}  // namespace agentspeed::remote
