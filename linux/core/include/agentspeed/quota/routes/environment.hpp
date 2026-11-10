#pragma once

#include <cstddef>
#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/fs_util.hpp"
#include "agentspeed/tz.hpp"

// Everything the phase 2 readers take from the computer, replaceable for tests and for the relay on a remote host
// (REMOTE 11): the home, the environment variables, the file system, the `/proc` root, a temporary folder, the
// machine's architecture and the local time zone.
namespace agentspeed::quota {

// The file system as the readers see it; tests count the accesses through it.
class Files {
public:
    virtual ~Files() = default;
    // Follows links; nullopt when the path is missing.
    virtual std::optional<fsx::Stat> stat(const std::string& path) = 0;
    // A regular file's bytes (following links) when it holds at most `cap` bytes; `/proc` files are read to their end.
    virtual std::optional<std::string> read(const std::string& path, std::size_t cap) = 0;
    // Up to `length` bytes of a regular file from `offset`.
    virtual std::optional<std::string> readRange(const std::string& path, std::int64_t offset, std::size_t length) = 0;
    // The names in a folder (not `.` and `..`); empty when it cannot be listed.
    virtual std::vector<std::string> list(const std::string& folder) = 0;
    virtual std::optional<std::string> readLink(const std::string& path) = 0;
    // Copies a regular file of at most `cap` bytes to `to`, which must not exist yet (created 0600, never followed).
    virtual bool copy(const std::string& from, const std::string& to, std::size_t cap) = 0;
};

// The real file system.
std::shared_ptr<Files> systemFiles();

struct VendorEnvironment {
    std::string home;
    // The app's (or the SSH command's) environment: GROK_HOME, KIMI_CODE_HOME, GEMINI_CLI_HOME, FACTORY_API_KEY,
    // XDG_CONFIG_HOME, TMPDIR. Read when needed, never copied.
    std::function<std::optional<std::string>(std::string_view)> variable;
    std::shared_ptr<Files> files;
    std::string procRoot = "/proc";
    // Where a browser's cookie store is copied for reading; empty: $TMPDIR, else /tmp.
    std::string tempFolder;
    // `uname -m` (`x86_64`, `aarch64`); empty: this machine's.
    std::string machine;
    // Devin's launch folders are local time; null: /etc/localtime.
    std::shared_ptr<const TimeZone> timeZone;

    // This computer: $HOME (else the passwd entry), getenv, the real file system.
    static VendorEnvironment system();
    // Tests: a home and fixed variables.
    static VendorEnvironment fixed(std::string homeFolder, std::map<std::string, std::string> values,
                                   std::shared_ptr<Files> fileSystem = nullptr);

    // A variable that is set and not empty.
    std::optional<std::string> get(std::string_view name) const;
    // A folder variable, `~/` expanded.
    std::optional<std::string> folder(std::string_view name) const;
    std::string inHome(std::string_view relative) const;
    // $XDG_CONFIG_HOME, else ~/.config.
    std::string configHome() const;
    std::string temporaryFolder() const;
    std::string architecture() const;
    std::shared_ptr<const TimeZone> zone() const;
    Files& fs() const;
};

}  // namespace agentspeed::quota
