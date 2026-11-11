#pragma once

#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <set>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include "agentspeed/monitor.hpp"
#include "channel.hpp"

// REMOTE 5.5: the host's monitor seeded like the desktop's, then polled; agents, parents, responses, tools and the
// source status go out as the protocol says.
namespace agentspeed::relay {

class LiveSession {
public:
    using Clock = std::function<std::int64_t()>;

    LiveSession(Output& out, std::set<SourceKind> enabled, Clock wallClock);
    ~LiveSession();
    LiveSession(const LiveSession&) = delete;
    LiveSession& operator=(const LiveSession&) = delete;

    // Seeds and sends the backlog (agents, parents, seeded responses), then `ready` and the first `status`.
    void start(std::int64_t todayStartMs);
    void setToday(std::int64_t todayStartMs) {
        todayStartMs_ = todayStartMs;
        statusDue_ = true;
    }
    void setWatch(bool on) { watch_ = on; }
    // QUOTAS 3: every Codex parser of every monitor (a restart's too) hands its `rate_limits` here. Set before `start`.
    void setRateLimitObserver(std::function<void(const json::Value&, std::int64_t)> observer) { rateLimits_ = std::move(observer); }
    // One tick of the live loop (every ~500 ms): a poll at most once a second, then whatever is due.
    void tick();

private:
    struct SentAgent {
        AgentInfo info;
        std::optional<std::string> cwd;
        const work::ActionReader* reader = nullptr;
        std::uint64_t actionsVersion = 0;
        std::string pendingText;
        std::uint64_t sentHash = 0;
        std::int64_t sentAtMs = 0;
        std::uint64_t seenTick = 0;
        bool pending = false, sent = false;
    };
    struct PendingResponse {
        ParsedResponse response;
        bool completion = false;
    };

    void build();
    void seed();
    void restart();
    // `scan`: the monitor read something, so agents, parents and the status are compared again.
    void flush(bool force, bool scan);
    void flushAgents(LineBatch& lines, bool force, bool scan);
    void scanAgents(LineBatch& lines);
    void flushParents(LineBatch& lines);
    void flushResponses(LineBatch& lines);
    void flushStatus(LineBatch& lines, bool force);
    void onResponse(const ParsedResponse& response, bool completion);
    void onTool(const std::string& agentId, SourceKind source, const work::Action& action);
    void onSeedProgress(std::size_t done, std::size_t total);

    Output& out_;
    const std::set<SourceKind> enabled_;
    Clock wallClock_;
    std::function<void(const json::Value&, std::int64_t)> rateLimits_;
    std::unique_ptr<Monitor> monitor_;
    std::int64_t todayStartMs_ = 0;
    bool watch_ = false;
    bool ready_ = false;
    std::uint64_t tick_ = 0;
    std::int64_t lastPollMs_ = 0, lastProgressMs_ = 0, lastStatusMs_ = 0;

    std::unordered_map<std::string, SentAgent> agents_;
    std::map<std::string, std::string> sentParents_;
    bool parentsSent_ = false;
    std::vector<std::string> responseOrder_;
    std::unordered_map<std::string, PendingResponse> responses_;
    // The keys an earlier monitor already sent: seeding a new one sends them again as updates (5.5, METRICS 4.4).
    std::unordered_set<std::string> resent_;
    std::vector<std::string> tools_;
    std::string statusText_;
    bool statusPending_ = false, statusDue_ = true;
};

}  // namespace agentspeed::relay
