#pragma once

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/quota/routes/environment.hpp"

// QUOTAS 9.4 Servers and Ports on Linux: the running Antigravity language servers and the loopback addresses to ask
// them on. Antigravity is never started. Only an `agy` process's own environment is read (two variables of it), never
// any other process's.
namespace agentspeed::quota {

struct AntigravityEndpoint {
    std::string scheme;  // http or https
    int port = 0;
    std::optional<std::string> token;
    bool operator==(const AntigravityEndpoint&) const = default;
};

struct AntigravityServer {
    std::int64_t pid = 0;
    // In the order asked; duplicates dropped (the first kept), at most 8.
    std::vector<AntigravityEndpoint> endpoints;
    bool operator==(const AntigravityServer&) const = default;
};
AntigravityServer antigravityServer(std::int64_t pid, const std::vector<AntigravityEndpoint>& endpoints);

namespace antigravity {

// A `language_server_*` executable whose arguments name `--app_data_dir` and antigravity.
bool isLanguageServer(std::string_view executable, const std::vector<std::string>& arguments);
// `--name value` or `--name=value`.
std::optional<std::string> flag(std::string_view name, const std::vector<std::string>& arguments);
// The extension server's port first (its own token, else `--csrf_token`), then each listening loopback port over
// http and then https with `--csrf_token`.
AntigravityServer languageServer(std::int64_t pid, const std::vector<std::string>& arguments, const std::vector<int>& listening);
// `agy`: `ANTIGRAVITY_LS_ADDRESS` (`host:port` on loopback) with `ANTIGRAVITY_CSRF_TOKEN`, then its listening ports.
std::optional<AntigravityServer> agy(std::int64_t pid, const std::optional<std::string>& token,
                                     const std::optional<std::string>& address, const std::vector<int>& listening);
// A daemon file `ls_*.json`: `{pid, csrfToken, httpsPort, httpPort}`.
std::optional<AntigravityServer> daemon(std::string_view text);
// `/proc/net/tcp` or `tcp6`: socket inode → port of the sockets listening on 127.0.0.1 or every address.
std::map<std::uint64_t, int> listeningSockets(std::string_view table);
// `NAME=value` entries of an environ block (NUL-separated), only the named ones.
std::map<std::string, std::string> environmentValues(std::string_view block, const std::vector<std::string_view>& names);

}  // namespace antigravity

class AntigravityLocator {
public:
    virtual ~AntigravityLocator() = default;
    virtual std::vector<AntigravityServer> servers() = 0;
};

// The user's processes from `<procRoot>` (their `exe` links, and the `cmdline` of a language server), each server's
// sockets from `<procRoot>/<pid>/fd` matched to `<procRoot>/net/tcp` and `tcp6`, and the daemon files of
// `~/.gemini/antigravity/daemon` and `~/.gemini/antigravity-ide/daemon` while their pid runs.
class ProcAntigravityLocator final : public AntigravityLocator {
public:
    explicit ProcAntigravityLocator(VendorEnvironment env) : env_(std::move(env)) {}
    std::vector<AntigravityServer> servers() override;

private:
    std::vector<int> listeningPorts(std::int64_t pid, std::optional<std::map<std::uint64_t, int>>& sockets);
    VendorEnvironment env_;
};

}  // namespace agentspeed::quota
