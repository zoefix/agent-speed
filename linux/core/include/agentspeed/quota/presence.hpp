#pragma once

#include <cstddef>
#include <cstdint>
#include <optional>
#include <set>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "agentspeed/quota/files.hpp"
#include "agentspeed/quota/window.hpp"
#include "agentspeed/traffic.hpp"

// QUOTAS 6: which sources' apps run and which are signed in, for the 額度 card.
namespace agentspeed::quota {

// Every source with a live agent process (METRICS 10.5's matcher, any source), plus the desktop apps' main processes:
// Claude Desktop and the Codex app. Feed it a process listing (traffic::sampler::ProcessReader::read()) every 15 s
// while an orb shows the card, or the traffic sampler's listing. Not thread-safe.
class RunningSources {
public:
    static constexpr std::int64_t kListIntervalMs = 15'000;

    explicit RunningSources(traffic::Env env) : table_(std::move(env)) {}

    // True when the running set changed.
    bool update(const std::vector<traffic::Process>& processes);
    bool runs(SourceKind source) const { return running_.contains(source); }
    const std::set<SourceKind>& running() const { return running_; }
    // Claude Desktop's main process is in this listing and was not in the one before (QUOTAS 4.4 search).
    bool claudeDesktopStarted() const { return desktopStarted_; }

private:
    traffic::ProcessTable table_;
    std::set<traffic::Key> listed_;
    std::set<SourceKind> running_;
    bool desktopStarted_ = false;
};

enum class SignInKind : std::uint8_t {
    Codex,       // auth.json: an object with `auth_mode == "chatgpt"` or a `tokens` object
    ClaudeCode,  // .claude.json: an `oauthAccount` object
};
// Only the named keys are looked at; no value is copied out.
bool signedInMarker(SignInKind kind, std::string_view bytes);

// One signed-in marker file (codexAuthFile(), claudeAccountFile()), parsed in memory and never copied, kept or logged:
// the bytes are wiped after the check. Read at most once a minute, and again when its size or modification time
// changes. Not thread-safe.
class SignInMarker {
public:
    static constexpr std::int64_t kRereadAfterMs = 60'000;
    static constexpr std::size_t kFileLimit = 64u << 20;

    SignInMarker(SignInKind kind, std::string file) : kind_(kind), file_(std::move(file)) {}

    // `enabled` false (the source disabled in Data sources or its quota switch off): nothing is read, not signed in.
    // Returns whether the marker says signed in.
    bool refresh(std::int64_t nowMs, bool enabled);
    bool signedIn() const { return signedIn_; }
    int reads() const { return reads_; }

private:
    SignInKind kind_;
    std::string file_;
    std::optional<files::Stamp> stamp_;
    std::optional<std::int64_t> readMs_;
    bool signedIn_ = false;
    int reads_ = 0;
};

// Claude Desktop is signed in while its snapshot is at most 24 hours old: Desktop only refreshes it while signed in and
// in use (when its window comes to the front, then every few minutes while it stays there), so a Desktop left in the
// background keeps an older one (QUOTAS 6).
constexpr std::int64_t kDesktopSignedInMs = 24 * 3'600'000LL;
bool desktopSignedIn(const std::optional<Snapshot>& snapshot, std::int64_t nowMs);

}  // namespace agentspeed::quota
