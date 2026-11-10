#pragma once

#include <set>
#include <string>
#include <vector>

#include "agentspeed/fs_util.hpp"
#include "agentspeed/monitor.hpp"
#include "agentspeed/remote_feed.hpp"
#include "fixture_support.hpp"

// Remote hosts in a monitor without ssh (REMOTE 7): the changes a host's feed hands over, built directly.
namespace remote_live {

using namespace agentspeed;

inline const std::string kA = "8C0D6B0A-1111-4222-8333-00000000000A";
inline const std::string kB = "8C0D6B0A-1111-4222-8333-00000000000B";
inline const std::string kLocal = "local";
constexpr std::int64_t kDay = 1'800'000'000'000;
constexpr std::int64_t kNow = kDay + 3'600'000;

inline std::string relayId(const std::string& name) { return "/home/u/.codex/sessions/" + name + ".jsonl"; }

inline std::string agentId(const std::string& host, const std::string& name = "a") {
    return host == kLocal ? "/local/" + name + ".jsonl" : remote::agentPrefix(host) + relayId(name);
}

inline ParsedResponse record(const std::string& key, const std::string& host, std::int64_t start, std::int64_t end, std::int64_t output,
                             const std::string& agent = "a", SourceKind source = SourceKind::Codex, bool final = true) {
    ParsedResponse r;
    r.key = key;
    r.source = source;
    r.agentId = agentId(host, agent);
    r.model = "gpt-5.5";
    r.startMs = start;
    r.endMs = end;
    r.tokens.input = 100;
    r.tokens.output = output;
    r.final = final;
    return r;
}

inline remote::RemoteAgent agent(const std::string& host, const std::string& name = "a", SourceKind source = SourceKind::Codex,
                                 Phase phase = Phase::Idle, std::optional<std::int64_t> inFlight = std::nullopt,
                                 std::int64_t lastLine = kNow - 1000) {
    remote::RemoteAgent a;
    a.hostId = host;
    a.id = agentId(host, name);
    a.relayId = relayId(name);
    a.source = source;
    a.project = "proj";
    a.cwd = "/home/u/proj";
    a.model = "gpt-5.5";
    a.phase = phase;
    a.inFlightStartMs = inFlight;
    a.lastLineMs = lastLine;
    return a;
}

inline remote::FeedChange response(const ParsedResponse& r, const std::string& host, bool live, bool completion = true) {
    return remote::feed::Response{remote::RemoteResponse{host, r, completion, "proj"}, live};
}

inline remote::FeedChange hostsOf(std::vector<remote::HostInfo> hosts) { return remote::feed::Hosts{std::move(hosts)}; }

inline remote::FeedChange status(const std::string& host, remote::StatusCode code) {
    remote::HostStatus s;
    s.status = remote::Status::of(code);
    return remote::feed::Status{host, s};
}

inline remote::FeedChange report(const std::string& host, const std::vector<std::tuple<SourceKind, bool, int>>& sources) {
    remote::SourceReport r;
    for (const auto& [source, available, sessions] : sources) r.sources.push_back({source, available, sessions, false, {}});
    return remote::feed::Report{host, r};
}

inline remote::FeedChange agentChange(const remote::RemoteAgent& a) { return remote::feed::Agent{a, std::nullopt}; }

// A monitor of the given sources whose roots do not exist: only remote hosts give it data.
inline MonitorConfig emptyConfig(std::set<SourceKind> enabled = {SourceKind::Codex, SourceKind::ClaudeCode}) {
    RootSettings s;
    for (auto* root : {&s.codex, &s.claude, &s.dsh, &s.openCode, &s.gemini, &s.omp, &s.antigravity, &s.workBuddy, &s.workBuddyAi, &s.zcode,
                       &s.kimi, &s.grokBuild, &s.pi, &s.openClaw, &s.hermes, &s.droid, &s.devin, &s.qoder, &s.qoderCli}) {
        *root = "/nonexistent-remote-test/root";
    }
    MonitorConfig config;
    config.locations = SourceLocations::resolve(s, processEnvironment(), "/nonexistent-home");
    config.enabled = std::move(enabled);
    return config;
}

// The codex fixtures as this computer's sources.
inline MonitorConfig fixtureConfig(std::set<SourceKind> enabled) {
    MonitorConfig config;
    config.enabled = std::move(enabled);
    RootSettings s;
    s.codex = agentspeed::fsx::normalize((fixtures::fixtureDir() / "codex/sessions").string());
    s.claude = agentspeed::fsx::normalize((fixtures::fixtureDir() / "claude/projects").string());
    for (auto* root : {&s.dsh, &s.openCode, &s.gemini, &s.omp, &s.antigravity, &s.workBuddy, &s.workBuddyAi, &s.zcode, &s.kimi,
                       &s.grokBuild, &s.pi, &s.openClaw, &s.hermes, &s.droid, &s.devin, &s.qoder, &s.qoderCli}) {
        *root = "/nonexistent-remote-test/root";
    }
    config.locations = SourceLocations::resolve(s, processEnvironment(), "/nonexistent-home");
    return config;
}

// A whole seeding of host `a`: two agents (b a child of a), two responses, the parent map and the source report.
inline std::vector<remote::FeedChange> seeding(const std::string& host, bool generating = false) {
    std::vector<remote::FeedChange> out{status(host, remote::StatusCode::Connecting), status(host, remote::StatusCode::Seeding)};
    out.push_back(agentChange(generating ? agent(host, "a", SourceKind::Codex, Phase::Generating, kNow - 1500, kNow - 200)
                                         : agent(host, "a", SourceKind::Codex, Phase::Idle, std::nullopt, kNow - 200)));
    out.push_back(agentChange(agent(host, "b", SourceKind::ClaudeCode, Phase::Idle, std::nullopt, kNow - 30'000)));
    out.push_back(response(record("k1", host, kNow - 60'000, kNow - 50'000, 500), host, false));
    out.push_back(response(record("k2", host, kNow - 40'000, kNow - 30'000, 300, "b", SourceKind::ClaudeCode), host, false));
    out.push_back(remote::feed::Parents{host, {{agentId(host, "b"), agentId(host, "a")}}});
    out.push_back(report(host, {{SourceKind::Codex, true, 1}, {SourceKind::ClaudeCode, true, 1}}));
    out.push_back(status(host, remote::StatusCode::Connected));
    return out;
}

inline const SourceRow& rowOf(const Snapshot& snap, SourceKind kind) {
    return *std::find_if(snap.sources.begin(), snap.sources.end(), [&](const SourceRow& r) { return r.source == kind; });
}

inline const AgentRow* agentRow(const Snapshot& snap, const std::string& id) {
    const auto it = std::find_if(snap.agents.begin(), snap.agents.end(), [&](const AgentRow& a) { return a.id == id; });
    return it == snap.agents.end() ? nullptr : &*it;
}

}  // namespace remote_live
