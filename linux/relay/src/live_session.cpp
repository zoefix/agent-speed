#include "live_session.hpp"

#include <algorithm>
#include <chrono>

#include "agentspeed/history.hpp"
#include "protocol.hpp"

namespace agentspeed::relay {

namespace {

constexpr std::int64_t kPollIntervalMs = 1000;
constexpr std::int64_t kAgentIntervalMs = 250;
constexpr std::int64_t kProgressIntervalMs = 250;
constexpr std::int64_t kStatusIntervalMs = 1000;
// METRICS 6.1: what the engine keeps beside today.
constexpr std::int64_t kRetentionMs = 30 * 60'000;
constexpr std::size_t kMaxToolsPerTick = 10'000;
// The complete parent map is one line (5.2); a map that would pass the line limit keeps the most recent children.
constexpr std::size_t kParentsBudget = kMaxOutputLine - 64 * 1024;

std::int64_t steadyNowMs() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

bool sameInfo(const AgentInfo& a, const AgentInfo& b) {
    return a.id == b.id && a.source == b.source && a.project == b.project && a.model == b.model && a.isSubagent == b.isSubagent &&
           a.phase == b.phase && a.inFlightStartMs == b.inFlightStartMs && a.lastLineMs == b.lastLineMs && a.modes == b.modes &&
           a.sessionModes == b.sessionModes && a.configuredModes == b.configuredModes;
}

}  // namespace

LiveSession::LiveSession(Output& out, std::set<SourceKind> enabled, Clock wallClock)
    : out_(out), enabled_(std::move(enabled)), wallClock_(std::move(wallClock)) {}

LiveSession::~LiveSession() = default;

void LiveSession::build() {
    monitor_ = std::make_unique<Monitor>(MonitorConfig::fromEnvironment({}, enabled_));
    // REMOTE 1 and 7: no socket on the host, and per-agent traffic is local only.
    monitor_->setTrafficEnabled(false);
    monitor_->setResponseObserver([this](const ParsedResponse& r, bool completion) { onResponse(r, completion); });
    monitor_->setToolObserver([this](const std::string& id, SourceKind source, const work::Action& a) { onTool(id, source, a); });
    monitor_->setSeedProgress([this](std::size_t done, std::size_t total) { onSeedProgress(done, total); });
    if (rateLimits_) monitor_->setRateLimitObserver(rateLimits_);
}

void LiveSession::seed() {
    monitor_->start(wallClock_(), todayStartMs_);
    monitor_->takeCompletions();
    lastPollMs_ = steadyNowMs();
}

void LiveSession::start(std::int64_t todayStartMs) {
    todayStartMs_ = todayStartMs;
    build();
    seed();
    LineBatch lines(out_);
    flushAgents(lines, true, true);
    flushParents(lines);
    flushResponses(lines);
    lines.add(readyLine());
    ready_ = true;
    flushStatus(lines, true);
}

void LiveSession::tick() {
    const auto now = steadyNowMs();
    const bool polled = now - lastPollMs_ >= kPollIntervalMs;
    if (polled) {
        lastPollMs_ = now;
        monitor_->poll(wallClock_(), todayStartMs_);
        // The relay raises no events: the app does, from the `response` lines (REMOTE 7).
        monitor_->takeCompletions();
        statusDue_ = true;
        if (monitor_->restartRequested()) restart();
    }
    flush(false, polled);
}

// A new root covers an old one (METRICS 4.7): a new monitor seeds inside the live phase; the keys the old one sent
// go out again as updates.
void LiveSession::restart() {
    flush(true, true);
    for (const auto& r : monitor_->responses()) resent_.insert(r.key);
    build();
    for (auto& [id, agent] : agents_) {
        agent.reader = nullptr;
        agent.actionsVersion = UINT64_MAX;
    }
    seed();
    flush(true, true);
    resent_.clear();
}

void LiveSession::flush(bool force, bool scan) {
    LineBatch lines(out_);
    flushAgents(lines, force, scan);
    if (scan) flushParents(lines);
    flushResponses(lines);
    for (auto& tool : tools_) lines.add(std::move(tool));
    tools_.clear();
    flushStatus(lines, force);
}

void LiveSession::flushAgents(LineBatch& lines, bool force, bool scan) {
    if (scan) scanAgents(lines);
    const auto now = steadyNowMs();
    for (auto& [id, agent] : agents_) {
        if (!agent.pending || (!force && agent.sent && now - agent.sentAtMs < kAgentIntervalMs)) continue;
        agent.sentHash = history::fnv1a(agent.pendingText);
        lines.add(std::move(agent.pendingText));
        agent.pendingText.clear();
        agent.pending = false;
        agent.sent = true;
        agent.sentAtMs = now;
    }
}

void LiveSession::scanAgents(LineBatch& lines) {
    ++tick_;
    for (const auto& [id, info] : monitor_->agents()) {
        const auto* reader = monitor_->actionsFor(id);
        const auto version = reader ? reader->log().version() : 0;
        auto cwd = monitor_->cwdOf(id);
        auto [it, fresh] = agents_.try_emplace(id);
        auto& agent = it->second;
        agent.seenTick = tick_;
        if (!fresh && sameInfo(agent.info, info) && agent.cwd == cwd && agent.reader == reader && agent.actionsVersion == version) continue;
        agent.info = info;
        agent.cwd = std::move(cwd);
        agent.reader = reader;
        agent.actionsVersion = version;
        auto text = agentLine({&agent.info, agent.cwd, reader ? &reader->actions() : nullptr});
        if (text.empty()) continue;
        if (agent.sent && history::fnv1a(text) == agent.sentHash) {
            agent.pending = false;
            agent.pendingText.clear();
        } else {
            agent.pending = true;
            agent.pendingText = std::move(text);
        }
    }
    for (auto it = agents_.begin(); it != agents_.end();) {
        if (it->second.seenTick == tick_) {
            ++it;
            continue;
        }
        if (it->second.sent) lines.add(agentRemovedLine(it->first));
        it = agents_.erase(it);
    }
}

void LiveSession::flushParents(LineBatch& lines) {
    const auto& parents = monitor_->parents();
    if (parentsSent_ && parents == sentParents_) return;
    sentParents_ = parents;
    parentsSent_ = true;
    std::vector<std::pair<std::string, std::string>> links(parents.begin(), parents.end());
    std::size_t bytes = 0;
    for (const auto& [child, parent] : links) bytes += child.size() + parent.size() + 8;
    if (bytes > kParentsBudget) {
        const auto& agents = monitor_->agents();
        const auto lastLine = [&](const std::string& id) {
            const auto it = agents.find(id);
            return it == agents.end() ? std::int64_t{0} : it->second.lastLineMs.value_or(0);
        };
        std::stable_sort(links.begin(), links.end(), [&](const auto& a, const auto& b) { return lastLine(a.first) > lastLine(b.first); });
        std::size_t kept = 0;
        bytes = 0;
        while (kept < links.size() && bytes + links[kept].first.size() + links[kept].second.size() + 8 <= kParentsBudget) {
            bytes += links[kept].first.size() + links[kept].second.size() + 8;
            ++kept;
        }
        links.resize(kept);
    }
    lines.add(parentsLine(links));
}

void LiveSession::flushResponses(LineBatch& lines) {
    if (responseOrder_.empty()) return;
    // What the store prunes at once is no use to the app either (METRICS 6.1, 6.6).
    const auto keepFrom = std::min(todayStartMs_, wallClock_() - kRetentionMs);
    const auto& agents = monitor_->agents();
    for (const auto& key : responseOrder_) {
        const auto& pending = responses_.at(key);
        if (pending.response.endMs < keepFrom) continue;
        std::optional<std::string> project;
        if (const auto agent = agents.find(pending.response.agentId); agent != agents.end()) project = agent->second.project;
        lines.add(responseLine(pending.response, pending.completion, project));
    }
    responseOrder_.clear();
    responses_.clear();
}

void LiveSession::flushStatus(LineBatch& lines, bool force) {
    if (!ready_) return;
    if (statusDue_) {
        statusDue_ = false;
        auto text = statusLine({monitor_->sourceStates(todayStartMs_), monitor_->warnings()});
        if (text != statusText_) {
            statusText_ = std::move(text);
            statusPending_ = true;
        }
    }
    const auto now = steadyNowMs();
    if (!statusPending_ || (!force && now - lastStatusMs_ < kStatusIntervalMs)) return;
    lines.add(statusText_);
    statusPending_ = false;
    lastStatusMs_ = now;
}

void LiveSession::onResponse(const ParsedResponse& response, bool completion) {
    if (resent_.contains(response.key)) completion = false;
    auto [it, inserted] = responses_.try_emplace(response.key);
    if (inserted) {
        responseOrder_.push_back(response.key);
        it->second.completion = completion;
    } else {
        it->second.completion = it->second.completion || completion;
    }
    it->second.response = response;
}

void LiveSession::onTool(const std::string& agentId, SourceKind source, const work::Action& action) {
    if (!watch_ || tools_.size() >= kMaxToolsPerTick) return;
    auto line = toolLine(agentId, source, action);
    if (!line.empty()) tools_.push_back(std::move(line));
}

void LiveSession::onSeedProgress(std::size_t done, std::size_t total) {
    if (ready_ || total == 0) return;
    const auto now = steadyNowMs();
    if (lastProgressMs_ != 0 && now - lastProgressMs_ < kProgressIntervalMs) return;
    lastProgressMs_ = now;
    out_.line(progressLine(static_cast<double>(done) / static_cast<double>(total)));
}

}  // namespace agentspeed::relay
